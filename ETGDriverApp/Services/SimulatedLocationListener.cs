using ETGDriverApp.Core.Models;
using ETGDriverApp.Core.Services;
using MauiLocation = Microsoft.Maui.Devices.Sensors.Location;

namespace ETGDriverApp.Services;

internal class SimulatedLocationListener(SimulatedVehicleState vehicle) : ILocationListener
{
    private const double AccuracyMeters = 8;

    // above AccuracyGate's (threshold - band) = 30m, so it classifies Borderline
    private const double DegradedAccuracyMeters = 42;

    // defaults chosen so the two are visibly distinct on the timeline:
    // degrade sits in Borderline for a while, blackout crosses the
    // watchdog's 20s HardThreshold into DeadReckoning
    public TimeSpan DegradeDuration { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan BlackoutDuration { get; set; } = TimeSpan.FromSeconds(35);

    private DateTimeOffset _degradedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _blackoutUntil = DateTimeOffset.MinValue;

    private readonly Lock _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private MauiLocation? _last;
    private LocationSessionState _state = LocationSessionState.Stopped;
    private double _heading;

    private EventHandler<MauiLocation>? _locationReceived;
    private EventHandler<SessionEndReason>? _sessionEndedUnexpectedly;

    // configured before StartAsync
    public (double Lat, double Lon) From { get; set; }
    public (double Lat, double Lon) To { get; set; }
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);
    public double SpeedMps { get; set; } = 12;

    public bool IsDegraded => DateTimeOffset.UtcNow < _degradedUntil;

    public bool IsBlackedOut => DateTimeOffset.UtcNow < _blackoutUntil;
    
    public MauiLocation? LastEmitted => _last;

    public void Degrade() => _degradedUntil = DateTimeOffset.UtcNow + DegradeDuration;

    // stops delivery entirely, forced fixes included, so the watchdog
    // actually escalates instead of being rescued
    public void Blackout() => _blackoutUntil = DateTimeOffset.UtcNow + BlackoutDuration;

    public event EventHandler? Arrived;

    event EventHandler<MauiLocation> ILocationListener.LocationReceived
    {
        add => _locationReceived += value;
        remove => _locationReceived -= value;
    }

    event EventHandler<SessionEndReason> ILocationListener.SessionEndedUnexpectedly
    {
        add => _sessionEndedUnexpectedly += value;
        remove => _sessionEndedUnexpectedly -= value;
    }

    LocationSessionState ILocationListener.State => _state;
    
    async Task<bool> ILocationListener.RequestPermissionAsync()
    {
        var whenInUse = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();

        if (whenInUse != PermissionStatus.Granted)
            whenInUse = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();

        if (whenInUse != PermissionStatus.Granted)
            return false;

        var always = await Permissions.CheckStatusAsync<Permissions.LocationAlways>();

        if (always != PermissionStatus.Granted)
            always = await Permissions.RequestAsync<Permissions.LocationAlways>();

        // the foreground service runs without it, so it isn't part of the result
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var notifications = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();

            if (notifications != PermissionStatus.Granted)
                await Permissions.RequestAsync<Permissions.PostNotifications>();
        }

        return always == PermissionStatus.Granted;
    }

    Task<bool> ILocationListener.StartAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_state == LocationSessionState.Running)
                return Task.FromResult(true);

            _cts = new CancellationTokenSource();
            _state = LocationSessionState.Running;
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        return Task.FromResult(true);
    }

    async Task ILocationListener.StopAsync(CancellationToken ct)
    {
        CancellationTokenSource? cts;
        Task? loop;

        lock (_sync)
        {
            if (_state == LocationSessionState.Stopped)
                return;

            _state = LocationSessionState.Stopped;
            (cts, loop, _cts, _loop) = (_cts, _loop, null, null);
        }

        if (cts is null)
            return;

        await cts.CancelAsync();

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
    }

    // the watchdog's escape hatch; replaying the last point keeps it quiet
    Task<MauiLocation?> ILocationListener.GetForcedFixAsync(CancellationToken ct) =>
        Task.FromResult(IsBlackedOut ? null : _last);

    private async Task RunAsync(CancellationToken ct)
    {
        var (lat, lon) = From;
        var remaining = Geo.DistanceMeters(From.Lat, From.Lon, To.Lat, To.Lon);
        _heading = Geo.BearingDegrees(From.Lat, From.Lon, To.Lat, To.Lon);
        
        // the route is one straight leg, so heading never changes and the
        // gyro sees only its own bias
        vehicle.HeadingDegrees = _heading;
        vehicle.HeadingRateDegPerSec = 0;
        vehicle.SpeedMps = SpeedMps;

        using var timer = new PeriodicTimer(TickInterval);

        Emit(lat, lon);

        // runs until StopAsync cancels; on arrival it parks at B and keeps
        // emitting, which is what lets the pickup dwell actually elapse
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (remaining > 0)
            {
                var step = Math.Min(SpeedMps * TickInterval.TotalSeconds, remaining);

                (lat, lon) = Geo.Project(lat, lon, _heading, step);
                remaining -= step;
                
                if (remaining <= 0)
                {
                    vehicle.SpeedMps = 0;
                    Arrived?.Invoke(this, EventArgs.Empty);
                }
            }

            Emit(lat, lon);
        }
    }
    
    private void Emit(double lat, double lon)
    {
        var location = new MauiLocation(lat, lon)
        {
            Accuracy = IsDegraded ? DegradedAccuracyMeters : AccuracyMeters,
            Speed = vehicle.SpeedMps,
            Course = _heading,
            Timestamp = DateTimeOffset.UtcNow
        };

        _last = location;

        // the vehicle keeps moving; the phone just stops hearing about it
        if (IsBlackedOut)
            return;

        _locationReceived?.Invoke(this, location);
    }
}