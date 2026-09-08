using CoreLocation;
using ETGDriverApp.Core.Services;
using MauiLocation = Microsoft.Maui.Devices.Sensors.Location;

namespace ETGDriverApp;

// Standard location updates, not significant-location-change monitoring:
// SLC wakes roughly every 500m, too coarse to reconstruct a route.
internal class AppleLocationListener : ILocationListener
{
    private static readonly TimeSpan ForcedFixTimeout = TimeSpan.FromSeconds(10);

    private readonly CLLocationManager _manager;
    private readonly LocationDelegate _delegate;

    private LocationSessionState _state = LocationSessionState.Stopped;
    private TaskCompletionSource<MauiLocation?>? _pendingForcedFix;

    private EventHandler<MauiLocation>? _locationReceived;
    private EventHandler<SessionEndReason>? _sessionEndedUnexpectedly;

    public AppleLocationListener()
    {
        _delegate = new LocationDelegate(OnLocationsUpdated, OnAuthorizationChanged);

        _manager = new CLLocationManager
        {
            Delegate = _delegate,
            DesiredAccuracy = CLLocation.AccuracyBest,
            DistanceFilter = CLLocationDistance.FilterNone,
            ActivityType = CLActivityType.AutomotiveNavigation,

            // without this, iOS pauses updates when it judges the device
            // stationary and does not resume them on its own
            PausesLocationUpdatesAutomatically = false
        };
    }

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

        // iOS often grants WhenInUse first and offers Always later through
        // its own prompt, so a false here is often not permanent
        var always = await Permissions.CheckStatusAsync<Permissions.LocationAlways>();

        if (always != PermissionStatus.Granted)
            always = await Permissions.RequestAsync<Permissions.LocationAlways>();

        return always == PermissionStatus.Granted;
    }

    Task<bool> ILocationListener.StartAsync(CancellationToken ct)
    {
        if (_state == LocationSessionState.Running)
            return Task.FromResult(true);

        var authorized = _manager.AuthorizationStatus
            is CLAuthorizationStatus.AuthorizedAlways
            or CLAuthorizationStatus.AuthorizedWhenInUse;

        if (!authorized)
            return Task.FromResult(false);

        // only legal with Always authorization; setting it otherwise throws
        if (_manager.AuthorizationStatus == CLAuthorizationStatus.AuthorizedAlways)
        {
            _manager.AllowsBackgroundLocationUpdates = true;

            if (OperatingSystem.IsIOSVersionAtLeast(11))
                _manager.ShowsBackgroundLocationIndicator = true;
        }

        _manager.StartUpdatingLocation();
        _state = LocationSessionState.Running;

        return Task.FromResult(true);
    }

    Task ILocationListener.StopAsync(CancellationToken ct)
    {
        if (_state == LocationSessionState.Stopped)
            return Task.CompletedTask;

        _state = LocationSessionState.Stopped;

        _manager.StopUpdatingLocation();
        _manager.AllowsBackgroundLocationUpdates = false;

        return Task.CompletedTask;
    }

    async Task<MauiLocation?> ILocationListener.GetForcedFixAsync(CancellationToken ct)
    {
        // RequestLocation cannot run while StartUpdatingLocation is active,
        // so this waits for the next delivery and nudges iOS to produce one
        var completion = new TaskCompletionSource<MauiLocation?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Interlocked.Exchange(ref _pendingForcedFix, completion);

        if (_state == LocationSessionState.Running)
        {
            _manager.StopUpdatingLocation();
            _manager.StartUpdatingLocation();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = Task.Delay(ForcedFixTimeout, timeoutCts.Token);

        var winner = await Task.WhenAny(completion.Task, timeout);

        if (winner != completion.Task)
        {
            Interlocked.CompareExchange(ref _pendingForcedFix, null, completion);

            var cached = _manager.Location;

            return cached is null ? null : ToMauiLocation(cached);
        }

        await timeoutCts.CancelAsync();

        return await completion.Task;
    }

    // the driver can revoke access from Settings without restarting the app
    private void OnAuthorizationChanged(CLAuthorizationStatus status)
    {
        if (_state != LocationSessionState.Running)
            return;

        if (status is CLAuthorizationStatus.AuthorizedAlways
            or CLAuthorizationStatus.AuthorizedWhenInUse)
            return;

        _state = LocationSessionState.Stopped;
        _manager.StopUpdatingLocation();

        _sessionEndedUnexpectedly?.Invoke(this, SessionEndReason.PermissionRevoked);
    }

    private void OnLocationsUpdated(CLLocation[] locations)
    {
        // batches arrive oldest first after a background wake; passed
        // through in order because the pipeline expects a monotonic stream
        foreach (var location in locations)
        {
            var converted = ToMauiLocation(location);

            _locationReceived?.Invoke(this, converted);

            var pending = Interlocked.Exchange(ref _pendingForcedFix, null);

            pending?.TrySetResult(converted);
        }
    }

    private static MauiLocation ToMauiLocation(CLLocation location) => new()
    {
        Latitude = location.Coordinate.Latitude,
        Longitude = location.Coordinate.Longitude,

        // iOS reports negative accuracy to mean invalid
        Accuracy = location.HorizontalAccuracy >= 0 ? location.HorizontalAccuracy : null,
        VerticalAccuracy = location.VerticalAccuracy >= 0 ? location.VerticalAccuracy : null,
        Altitude = location.Altitude,
        Course = location.Course >= 0 ? location.Course : null,
        Speed = location.Speed >= 0 ? location.Speed : null,
        Timestamp = (DateTime)location.Timestamp
    };

    private class LocationDelegate(
        Action<CLLocation[]> onLocations,
        Action<CLAuthorizationStatus> onAuthorizationChanged) : CLLocationManagerDelegate
    {
        public override void LocationsUpdated(CLLocationManager manager, CLLocation[] locations) =>
            onLocations(locations);

        public override void DidChangeAuthorization(CLLocationManager manager) =>
            onAuthorizationChanged(manager.AuthorizationStatus);

        public override void Failed(CLLocationManager manager, Foundation.NSError error)
        {
            // kCLErrorLocationUnknown is transient; a real outage is handled
            // by the staleness watchdog
        }
    }
}
