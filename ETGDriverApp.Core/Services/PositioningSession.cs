using ETGDriverApp.Core.Models;
using ETGDriverApp.Core.Services.DeadReckoning;
using ETGDriverApp.Core.Services.Sites;

namespace ETGDriverApp.Core.Services;

public interface IPositioningSession
{
    bool IsRunning { get; }

    // the OS ended a running session; IsRunning is already false
    event EventHandler<SessionEndReason> SessionEndedUnexpectedly;

    // must be called while the app is visible
    Task<StartResult> StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);
}

public record StartResult(
    bool Started,
    bool HasBackgroundPermission,
    IReadOnlyList<NormalizedPosition> RecoveredTrack,
    StartFailure? Failure = null);

public enum StartFailure
{
    PermissionDenied,
    NotVisible,
    NoLocationProvider
}

// Owns the lifetime of a shift. Nothing below is constructed here - every
// collaborator is a process-lifetime singleton - so this is the only place
// that knows when a shift begins and ends, and therefore the only place that
// can wind the subsystems down. Start and stop must stay symmetric: the bug
// this replaces was three subsystems started and two stopped.
internal class PositioningSession(
    ISessionRecovery recovery,
    ILocationListener listener,
    PositionFeed feed,
    DeadReckoningFeed deadReckoningFeed,
    IStalenessWatchdog watchdog,
    IPositionFilterPipeline pipeline,
    IGeofenceEvaluator geofenceEvaluator,
    ISiteArrivalMonitor siteMonitor) : IPositioningSession
{
    private readonly Lock _sync = new();

    private bool _isRunning;
    private bool _subscribed;
    private bool _endSubscribed;

    private EventHandler<SessionEndReason>? _sessionEndedUnexpectedly;

    bool IPositioningSession.IsRunning => _isRunning;

    event EventHandler<SessionEndReason> IPositioningSession.SessionEndedUnexpectedly
    {
        add => _sessionEndedUnexpectedly += value;
        remove => _sessionEndedUnexpectedly -= value;
    }

    async Task<StartResult> IPositioningSession.StartAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_isRunning)
                return new StartResult(true, true, []);
        }

        // recover before anything writes new fixes to the store
        var recovered = await recovery.RecoverAsync(ct);

        var hasBackground = await listener.RequestPermissionAsync();

        var started = await feed.StartAsync(ct);

        if (!started)
        {
            return new StartResult(
                false, hasBackground, recovered.RecoveredTrack,
                hasBackground ? StartFailure.NotVisible : StartFailure.PermissionDenied);
        }

        lock (_sync)
        {
            if (!_subscribed)
            {
                pipeline.PositionUpdated += OnPositionUpdated;
                _subscribed = true;
            }

            if (!_endSubscribed)
            {
                listener.SessionEndedUnexpectedly += OnSessionEndedUnexpectedly;
                _endSubscribed = true;
            }

            _isRunning = true;
        }

        deadReckoningFeed.Start();
        watchdog.Start();

        return new StartResult(true, hasBackground, recovered.RecoveredTrack);
    }

    async Task IPositioningSession.StopAsync(CancellationToken ct) => await StopCoreAsync(ct);

    // One teardown path. StopAsync and the OS-kill handler used to each have
    // their own copy, which is how they drifted: neither stopped dead
    // reckoning, and neither unsubscribed the geofence handler.
    private async Task StopCoreAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (!_isRunning)
                return;

            _isRunning = false;
        }

        // Order matters. The watchdog calls into PositionFeed.ForcedFixAsync,
        // so it has to be quiet before the feed goes; dead reckoning ingests
        // into the pipeline, so it goes before we stop listening to it.
        await watchdog.StopAsync();
        await deadReckoningFeed.StopAsync();
        await feed.StopAsync(ct);

        lock (_sync)
        {
            if (_subscribed)
            {
                // otherwise a stopped session keeps evaluating the last
                // shift's fences - a driver parked at home inside an old
                // pickup radius could still trigger transitions
                pipeline.PositionUpdated -= OnPositionUpdated;
                _subscribed = false;
            }

            if (_endSubscribed)
            {
                listener.SessionEndedUnexpectedly -= OnSessionEndedUnexpectedly;
                _endSubscribed = false;
            }
        }

        // Belt and braces with the unsubscribe above, and deliberately so.
        // Which sites are worth watching is the caller's business, but the
        // guarantee that a finished shift watches none of them is not
        // something to leave to a host remembering to call Unwatch.
        siteMonitor.UnwatchAll();
    }

    private void OnPositionUpdated(object? sender, NormalizedPosition position) =>
        geofenceEvaluator.OnPositionUpdated(position);

    private async void OnSessionEndedUnexpectedly(object? sender, SessionEndReason reason)
    {
        // IsRunning is already false by the time subscribers are told, which
        // is the documented contract of the event
        await StopCoreAsync(CancellationToken.None);

        _sessionEndedUnexpectedly?.Invoke(this, reason);
    }
}
