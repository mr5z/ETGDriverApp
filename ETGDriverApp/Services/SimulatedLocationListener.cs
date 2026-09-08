using ETGDriverApp.Core.Models;
using ETGDriverApp.Core.Services;
using MauiLocation = Microsoft.Maui.Devices.Sensors.Location;

namespace ETGDriverApp.Services;

internal class SimulatedLocationListener(SimulatedVehicleState vehicle) : ILocationListener
{
    private const double AccuracyMeters = 8;

    // above AccuracyGate's (threshold - band) = 30m, so it classifies Borderline
    private const double DegradedAccuracyMeters = 42;

    // a waypoint counts as reached once inside this, so the pursuit doesn't
    // circle a point it can never land exactly on
    private const double WaypointReachedMeters = 20;

    // ~4s for a 90 degree corner at 22.5 deg/s, which is roughly a real
    // intersection and well inside what heading integration can follow
    private const double TurnRateDegPerSec = 22.5;

    // below this the tick counts as straight running, so HeadingRateDegPerSec
    // is exactly 0 on a leg and the gyro sees only its bias
    private const double StraightThresholdDegPerSec = 0.5;

    // vehicles slow for corners; this also keeps the pursuit radius sane
    private const double CorneringSpeedFactor = 0.5;

    // defaults chosen so the two are visibly distinct on the timeline:
    // degrade sits in Borderline for a while, blackout crosses the
    // watchdog's 20s HardThreshold into DeadReckoning
    public TimeSpan DegradeDuration { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan BlackoutDuration { get; set; } = TimeSpan.FromMinutes(5);

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

    // intermediate corners between From and To. Empty means the old
    // single straight leg; anything else makes the route turn.
    public IReadOnlyList<(double Lat, double Lon)> Waypoints { get; set; } = [];

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

    // fired as each corner is passed, so the log can be lined up against
    // the heading-offset trace
    public event EventHandler<int>? WaypointReached;

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
            _loop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
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
        var route = BuildRoute();
        var leg = 0;

        // start already pointing down the first leg, so the run doesn't open
        // with a turn the vehicle would never actually make
        _heading = Geo.BearingDegrees(lat, lon, route[0].Lat, route[0].Lon);

        vehicle.HeadingDegrees = _heading;
        vehicle.HeadingRateDegPerSec = 0;
        vehicle.SpeedMps = SpeedMps;

        var dt = TickInterval.TotalSeconds;

        using var timer = new PeriodicTimer(TickInterval);

        Emit(lat, lon);

        // runs until StopAsync cancels; on arrival it parks at the last
        // waypoint and keeps emitting, which is what lets the pickup dwell
        // actually elapse
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (leg < route.Count)
            {
                var target = route[leg];

                // steer toward the waypoint at a bounded rate rather than
                // snapping; the result is a pursuit curve, which is close
                // enough to a real corner and cuts it the way a car does
                var desired = Geo.BearingDegrees(lat, lon, target.Lat, target.Lon);
                var delta = SignedDelta(_heading, desired);
                var maxTurn = TurnRateDegPerSec * dt;
                var turn = Math.Clamp(delta, -maxTurn, maxTurn);

                _heading = Geo.NormalizeDegrees(_heading + turn);

                var rate = turn / dt;

                if (Math.Abs(rate) < StraightThresholdDegPerSec)
                    rate = 0;

                var speed = rate == 0 ? SpeedMps : SpeedMps * CorneringSpeedFactor;
                var remaining = Geo.DistanceMeters(lat, lon, target.Lat, target.Lon);
                var step = Math.Min(speed * dt, remaining);

                (lat, lon) = Geo.Project(lat, lon, _heading, step);

                vehicle.HeadingDegrees = _heading;
                vehicle.HeadingRateDegPerSec = rate;
                vehicle.SpeedMps = speed;

                if (Geo.DistanceMeters(lat, lon, target.Lat, target.Lon) <= WaypointReachedMeters)
                {
                    leg++;

                    if (leg < route.Count)
                    {
                        WaypointReached?.Invoke(this, leg);
                    }
                    else
                    {
                        vehicle.HeadingRateDegPerSec = 0;
                        vehicle.SpeedMps = 0;
                        Arrived?.Invoke(this, EventArgs.Empty);
                    }
                }
            }

            Emit(lat, lon);
        }
    }

    private IReadOnlyList<(double Lat, double Lon)> BuildRoute() =>
        Waypoints.Count == 0 ? [To] : [.. Waypoints, To];

    // shortest signed turn from a to b, in -180..180
    private static double SignedDelta(double from, double to)
    {
        var delta = Geo.NormalizeDegrees(to - from);

        return delta > 180 ? delta - 360 : delta;
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