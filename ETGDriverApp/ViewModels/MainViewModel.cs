using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ETGDriverApp.Core.Models;
using ETGDriverApp.Core.Services;
using ETGDriverApp.Core.Services.Jobs;
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
    private static readonly (double Lat, double Lon) Origin = (14.3370, 121.0800);

    private readonly IPositioningSession _session;
    private readonly IPositionFilterPipeline _pipeline;
    private readonly IJobSiteMonitor _jobs;
    private readonly SimulatedLocationListener _simulator;
    private readonly NoOpJobStatusWriter _statusWriter;
    private readonly Random _random = new();
    private readonly List<JobAssignment> _armed = [];
    private NormalizedPosition? _lastPosition;

    public string? OnSiteJobId => NearestArmedJobId();
    public ObservableCollection<MapCircleViewModel> MapCircles { get; } = [];

    private MapPointViewModel? _driverPin;
    private (double Lat, double Lon)? _destination;
    private MapCircleViewModel? _accuracyCircle;

    public MainViewModel(
        IPositioningSession session,
        IPositionFilterPipeline pipeline,
        IJobSiteMonitor jobs,
        SimulatedLocationListener simulator,
        NoOpJobStatusWriter statusWriter)
    {
        _session = session;
        _pipeline = pipeline;
        _jobs = jobs;
        _simulator = simulator;
        _statusWriter = statusWriter;

        _pipeline.PositionUpdated += OnPositionUpdated;
        _pipeline.PositionEvaluated += OnPositionEvaluated;
        _pipeline.LocationUnavailable += (_, e) => Append("LocationUnavailable — _current cleared");
        _simulator.Arrived += (_, _) => Append("Arrived at destination (holding position)");
        _statusWriter.StatusWritten += (_, message) => Append(message);
        _jobs.OnSiteAvailable += (_, e) => Append($"ON_SITE available {e.JobId} ({e.Mode}/{e.Confidence})");
        _jobs.OnSiteSet += (_, e) => Append($"ON_SITE set {e.JobId} via {e.Trigger}");

        CameraCenter = new MauiLocation(Origin.Lat, Origin.Lon);
    }

    public ObservableCollection<MapPointViewModel> MapPoints { get; } = [];

    // your logs go here
    public ObservableCollection<string> LogEntries { get; } = [];

    public MauiLocation? CameraCenter { get; set; }

    public double CameraRadiusMeters { get; set; } = 1500;

    public string StatusText { get; set; } = "Idle";

    public bool IsTripRunning { get; set; }

    public string TripButtonText => IsTripRunning ? "Stop Trip" : "Start Trip";

    [RelayCommand]
    private void AddRandomJob()
    {
        var jobId = $"SIM-{_random.Next(1000, 9999)}";
        var lat = Origin.Lat + (_random.NextDouble() - 0.5) * 0.02;
        var lon = Origin.Lon + (_random.NextDouble() - 0.5) * 0.02;
        const double radius = 120;
        
        var job = new JobAssignment(jobId, new JobSite(lat, lon, radius, OnSiteMode.Automatic));

        _armed.Add(job);
        _jobs.ArmForJob(job);

        _destination ??= (lat, lon);

        MapPoints.Add(new MapPointViewModel
        {
            Location = new MauiLocation(lat, lon),
            Label = $"Pickup {jobId}",
            Detail = $"r={radius:F0}m"
        });
        
        MapCircles.Add(new MapCircleViewModel
        {
            Center = new MauiLocation(lat, lon),
            RadiusMeters = radius,
            StrokeColor = Colors.SeaGreen,
            FillColor = Colors.SeaGreen.WithAlpha(0.15f)
        });

        CameraCenter = new MauiLocation(lat, lon);
        Append($"Armed {jobId} @ {lat:F5},{lon:F5}");
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
    
    [RelayCommand(CanExecute = nameof(CanConfirmOnSite))]
    private async Task ConfirmOnSiteAsync()
    {
        if (NearestArmedJobId() is not { } jobId)
            return;

        await _jobs.ConfirmOnSiteAsync(jobId);

        Append($"Driver confirmed ON_SITE for {jobId}");
    }
    
    private bool CanConfirmOnSite() => NearestArmedJobId() is not null;
    
    private string? NearestArmedJobId()
    {
        if (_lastPosition is not { } position)
            return null;

        return _armed
            .Where(job => Geo.DistanceMeters(
                              position.Latitude, position.Longitude,
                              job.Pickup.Latitude, job.Pickup.Longitude)
                          <= job.Pickup.GeofenceRadiusMeters)
            .Select(job => job.JobId)
            .FirstOrDefault();
    }

    private async Task StopTripAsync()
    {
        if (!IsTripRunning)
            return;

        IsTripRunning = false;

        await _session.StopAsync();

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
            CameraRadiusMeters = 400;
            StatusText = $"{position.State} · ±{position.EffectiveRadiusMeters:F0}m · " +
                         $"{position.Latitude:F5},{position.Longitude:F5}";
            
            ConfirmOnSiteCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(OnSiteJobId)));
        });
    
    private void OnPositionEvaluated(object? sender, PositionEvaluatedEventArgs e)
    {
        if (!e.Accepted)
            Append($"Rejected {e.Sample.SourceType}: {e.Reason} {e.Diagnostics}");
    }

    private void Append(string message) =>
        MainThread.BeginInvokeOnMainThread(() =>
            LogEntries.Add($"{DateTime.Now:HH:mm:ss} {message}"));
}