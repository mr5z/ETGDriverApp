using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ETGDriverApp.Core.Diagnostics;
using ETGDriverApp.Core.Helpers;
using ETGDriverApp.Core.Models;
using ETGDriverApp.Core.Services;
using ETGDriverApp.Core.Services.Sites;
using ETGDriverApp.Services;
using Nkraft.MvvmEssentials.ViewModels;
using PropertyChanged;
using MauiLocation = Microsoft.Maui.Devices.Sensors.Location;

namespace ETGDriverApp.ViewModels;

[AddINotifyPropertyChangedInterface]
public class MapPointViewModel
{
    public required MauiLocation Location { get; set; }

    public required string Label { get; set; }

    public string? Detail { get; set; }
}

[AddINotifyPropertyChangedInterface]
public class MapCircleViewModel
{
    public required MauiLocation Center { get; set; }

    public required double RadiusMeters { get; set; }

    public Color StrokeColor { get; set; } = Colors.SlateBlue;

    public Color FillColor { get; set; } = Colors.SlateBlue.WithAlpha(0.15f);
}

internal partial class MainViewModel : PageViewModel
{
    private static readonly (double Lat, double Lon) Origin = (15.6175722, 120.9382684);

    // The job shape lives here now, not in Core. Core watches places; what a
    // place is for, and which one comes next, is ours.
    private sealed record SimSite(double Lat, double Lon, double Radius);

    private sealed record SimJob(string JobId, IReadOnlyList<SimSite> Sites);

    private readonly IPositioningSession _session;
    private readonly IPositionFilterPipeline _pipeline;
    private readonly ISiteArrivalMonitor _sites;
    private readonly SimulatedLocationListener _simulator;
    private readonly Random _random = new();

    private readonly Dictionary<string, SimJob> _jobs = [];

    // sites with an arrival worth offering, against the confidence behind
    // each one; only touched on the main thread
    private readonly Dictionary<string, GeofenceEventConfidence> _arrivalAvailable = [];

    private NormalizedPosition? _lastPosition;

    private MapPointViewModel? _driverPin;
    private (double Lat, double Lon)? _destination;
    private MapCircleViewModel? _accuracyCircle;

    public MainViewModel(
        IPositioningSession session,
        IPositionFilterPipeline pipeline,
        IPositionStateMachine stateMachine,
        ISiteArrivalMonitor sites,
        IPositioningDiagnostics diagnostics,
        SimulatedLocationListener simulator)
    {
        _session = session;
        _pipeline = pipeline;
        _sites = sites;
        _simulator = simulator;

        _pipeline.PositionUpdated += OnPositionUpdated;
        _pipeline.PositionEvaluated += OnPositionEvaluated;
        _pipeline.LocationUnavailable += (_, _) => Append("LocationUnavailable — position no longer defensible");
        _simulator.Arrived += (_, _) => Append("Arrived at destination (holding position)");
        _sites.ArrivalAvailable += OnArrivalAvailable;
        _sites.ArrivalLapsed += OnArrivalLapsed;
        _sites.ArrivalContradicted += OnArrivalContradicted;
        _sites.SiteLeft += OnSiteLeft;
        stateMachine.StateChanged += (_, state) => Append($"State -> {state}");
        diagnostics.Traced += OnTraced;

        CameraCenter = new MauiLocation(Origin.Lat, Origin.Lon);
    }

    public string? OnSiteSiteKey => _arrivalAvailable.Keys.FirstOrDefault();

    // The button stays tappable on an unverified arrival, but says so. The
    // driver can see out of the windscreen; the positioning stack cannot.
    public string OnSiteButtonText =>
        OnSiteSiteKey is { } siteKey &&
        _arrivalAvailable.TryGetValue(siteKey, out var confidence) &&
        confidence != GeofenceEventConfidence.Trusted
            ? "On Site?"
            : "On Site";

    public ObservableCollection<MapPointViewModel> MapPoints { get; } = [];

    public ObservableCollection<MapCircleViewModel> MapCircles { get; } = [];

    // your logs go here
    public ObservableCollection<string> LogEntries { get; } = [];

    public MauiLocation? CameraCenter { get; set; }

    public double CameraRadiusMeters { get; set; } = 400;

    public string StatusText { get; set; } = "Idle";

    public bool IsTripRunning { get; set; }

    public string TripButtonText => IsTripRunning ? "Stop Trip" : "Start Trip";

    [RelayCommand]
    private void AddRandomJob()
    {
        var jobId = $"SIM-{_random.Next(1000, 9999)}";

        // Two sites, so the sequencing the monitor no longer does is
        // exercised: only the first is watched now, and the second is armed
        // when the first is left. That ordering constraint - a nearby second
        // site must not fire while the driver is still at the first - is
        // exactly what moved out of Core.
        var sites = new List<SimSite>
        {
            RandomSite(),
            RandomSite()
        };

        var job = new SimJob(jobId, sites);

        _jobs[jobId] = job;

        for (var i = 0; i < sites.Count; i++)
            DrawSite(jobId, i, sites[i], armed: i == 0);

        WatchSite(jobId, 0);

        _destination ??= (sites[0].Lat, sites[0].Lon);

        CameraCenter = new MauiLocation(sites[0].Lat, sites[0].Lon);
        Append($"Armed {jobId} site 0 of {sites.Count} @ {sites[0].Lat:F5},{sites[0].Lon:F5}");
    }

    [RelayCommand]
    private async Task ToggleTripAsync()
    {
        if (IsTripRunning)
        {
            await StopTripAsync();

            return;
        }

        _simulator.From = _lastPosition is { } p ? (p.Latitude, p.Longitude) : Origin;
        _simulator.To = _destination ?? (Origin.Lat + 0.01, Origin.Lon + 0.01);

        var result = await _session.StartAsync();

        IsTripRunning = result.Started;

        Append(result.Started
            ? $"Trip started -> {_simulator.To.Lat:F5},{_simulator.To.Lon:F5}"
            : $"Start failed: {result.Failure}");
    }

    [RelayCommand]
    private void DegradeAccuracy()
    {
        _simulator.Degrade();

        Append($"Accuracy degraded for {_simulator.DegradeDuration.TotalSeconds:F0}s");
    }

    [RelayCommand]
    private void Blackout()
    {
        _simulator.Blackout();

        Append($"Signal blackout for {_simulator.BlackoutDuration.TotalSeconds:F0}s");
    }

    [RelayCommand]
    private async Task RequestPermissionsAsync()
    {
        var whenInUse = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        var always = await Permissions.CheckStatusAsync<Permissions.LocationAlways>();

        Append($"Before: WhenInUse={whenInUse}, Always={always}");

        var hasBackground = await ((ILocationListener)_simulator).RequestPermissionAsync();

        whenInUse = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        always = await Permissions.CheckStatusAsync<Permissions.LocationAlways>();

        Append($"After: WhenInUse={whenInUse}, Always={always}, background={hasBackground}");

        if (whenInUse == PermissionStatus.Granted && !hasBackground)
            Append("Foreground-only: grant 'Allow all the time' in app Settings");
    }

    // No longer async: confirming writes nothing, so there is nothing to
    // await and nothing to roll back.
    [RelayCommand(CanExecute = nameof(CanConfirmOnSite))]
    private void ConfirmOnSite()
    {
        if (OnSiteSiteKey is not { } siteKey)
            return;

        if (_sites.Confirm(siteKey) is not { } confirmation)
            return;

        _arrivalAvailable.Remove(siteKey);
        RefreshOnSite();

        Append($"Driver confirmed arrival {siteKey} " +
               $"(basis {confirmation.BasisConfidence?.ToString() ?? "none"})");
    }

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        if (LogEntries.Count == 0)
            return;

        await Clipboard.SetTextAsync(string.Join(Environment.NewLine, LogEntries));

        Append($"Copied {LogEntries.Count} lines to clipboard");
    }

    [RelayCommand]
    private void ClearLogs() => LogEntries.Clear();

    private bool CanConfirmOnSite() => OnSiteSiteKey is not null;

    private void RefreshOnSite()
    {
        ConfirmOnSiteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(OnSiteSiteKey)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(OnSiteButtonText)));
    }

    private SimSite RandomSite()
    {
        // far enough that a five-minute blackout at 12 m/s (3.6 km) plus the
        // settling and reacquisition legs all fit inside one trip
        const double minDistanceMeters = 2000;
        const double maxDistanceMeters = 4000;
        const double radius = 120;

        var bearing = _random.NextDouble() * 360;
        var distance = minDistanceMeters +
                       _random.NextDouble() * (maxDistanceMeters - minDistanceMeters);

        var (lat, lon) = Geo.Project(Origin.Lat, Origin.Lon, bearing, distance);

        return new SimSite(lat, lon, radius);
    }

    // The exit factor was PickupExitRadiusFactor, a constant in the old
    // monitor. It is a property of the place now, so the caller sets it.
    private void WatchSite(string jobId, int index)
    {
        var site = _jobs[jobId].Sites[index];

        _sites.Watch(
            SiteKey(jobId, index),
            new SiteDefinition(
                site.Lat, site.Lon, site.Radius, ExitRadiusFactor: 1.5));
    }

    private void DrawSite(string jobId, int index, SimSite site, bool armed)
    {
        MapPoints.Add(new MapPointViewModel
        {
            Location = new MauiLocation(site.Lat, site.Lon),
            Label = $"{jobId} site {index}",
            Detail = $"r={site.Radius:F0}m{(armed ? "" : " (not yet watched)")}"
        });

        MapCircles.Add(new MapCircleViewModel
        {
            Center = new MauiLocation(site.Lat, site.Lon),
            RadiusMeters = site.Radius,
            StrokeColor = armed ? Colors.SeaGreen : Colors.Gray,
            FillColor = (armed ? Colors.SeaGreen : Colors.Gray).WithAlpha(0.15f)
        });
    }

    private static string SiteKey(string jobId, int index) => $"{jobId}:{index}";

    private static (string JobId, int Index) ParseSiteKey(string siteKey)
    {
        var split = siteKey.LastIndexOf(':');

        return (siteKey[..split], int.Parse(siteKey[(split + 1)..]));
    }

    private void OnArrivalAvailable(object? sender, SiteArrivalEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _arrivalAvailable[e.SiteKey] = e.Confidence;
            RefreshOnSite();

            Append($"Arrival available {e.SiteKey} ({e.Confidence})");
        });

    // The offer is withdrawn. Nothing was confirmed, so nothing is being
    // taken back from the driver - the button simply goes away.
    private void OnArrivalLapsed(object? sender, SiteArrivalEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _arrivalAvailable.Remove(e.SiteKey);
            RefreshOnSite();

            Append($"Arrival lapsed {e.SiteKey} (was {e.Confidence})");
        });

    private void OnArrivalContradicted(object? sender, ArrivalContradictedEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _arrivalAvailable.Remove(e.SiteKey);
            RefreshOnSite();

            Append($"Arrival contradicted {e.SiteKey} — confirmed at {e.Dropped.At:HH:mm:ss} " +
                   $"on {e.Dropped.BasisConfidence?.ToString() ?? "no"} evidence");
        });

    // Sequencing. Core told us a site was left; what that means for the job,
    // and which site is next, is ours to decide.
    private void OnSiteLeft(object? sender, SiteLeftEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Append($"Left site {e.SiteKey} ({e.Confidence})");

            var (jobId, index) = ParseSiteKey(e.SiteKey);

            if (!_jobs.TryGetValue(jobId, out var job))
                return;

            // this site has no further role
            _sites.Unwatch(e.SiteKey);

            var next = index + 1;

            if (next >= job.Sites.Count)
            {
                Append($"Job {jobId} has no further sites");

                return;
            }

            WatchSite(jobId, next);

            Append($"Armed {jobId} site {next} of {job.Sites.Count}");
        });

    private async Task StopTripAsync()
    {
        if (!IsTripRunning)
            return;

        IsTripRunning = false;

        await _session.StopAsync();

        // the session unwatches everything on teardown, so our own view of
        // what is offered has to go with it
        _arrivalAvailable.Clear();
        RefreshOnSite();

        Append("Trip stopped");
    }

    private void OnPositionUpdated(object? sender, NormalizedPosition position) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _lastPosition = position;
            var location = new MauiLocation(position.Latitude, position.Longitude);

            if (_driverPin is not null)
                MapPoints.Remove(_driverPin);

            if (_accuracyCircle is not null)
                MapCircles.Remove(_accuracyCircle);

            // Pin re-renders on collection change, not on a mutated Location
            _driverPin = new MapPointViewModel
            {
                Location = location,
                Label = "Driver",
                Detail = position.State.ToString()
            };
            MapPoints.Add(_driverPin);

            _accuracyCircle = new MapCircleViewModel
            {
                Center = location,
                RadiusMeters = Math.Max(position.EffectiveRadiusMeters, 5),
                StrokeColor = Colors.OrangeRed,
                FillColor = Colors.OrangeRed.WithAlpha(0.12f)
            };
            MapCircles.Add(_accuracyCircle);

            CameraCenter = location;
            CameraRadiusMeters = 600;
            StatusText = $"{position.State} · ±{position.EffectiveRadiusMeters:F0}m · " +
                         $"{position.Latitude:F5},{position.Longitude:F5}";

            if (position.SourceType == PositionSourceType.DeadReckoned)
                Append($"DR estimate {position.Latitude:F5},{position.Longitude:F5}");

            if (position.SourceType == PositionSourceType.DeadReckoned &&
                _simulator.LastEmitted?.Location is { } truth)
            {
                var error = Geo.DistanceMeters(
                    position.Latitude, position.Longitude, truth.Latitude, truth.Longitude);

                Append($"DR error {error:F0}m");
            }
        });

    private void OnPositionEvaluated(object? sender, PositionEvaluatedEventArgs e)
    {
        if (!e.Accepted)
            Append($"Rejected {e.Sample.SourceType}: {e.Reason} {e.Diagnostics}");
    }

    private void OnTraced(object? sender, PositioningTraceEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() => Append($"[{e.Category}] {e.Message}"));

    private void Append(string message) =>
        MainThread.BeginInvokeOnMainThread(() =>
            LogEntries.Add($"{DateTime.Now:HH:mm:ss} {message}"));
}