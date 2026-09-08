using ETGDriverApp.Core.Models;
using ETGDriverApp.Core.Services.DeadReckoning;

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

internal class PositioningSession(
    ISessionRecovery recovery,
    ILocationListener listener,
    PositionFeed feed,
    DeadReckoningFeed deadReckoningFeed,
    IStalenessWatchdog watchdog,
    IPositionFilterPipeline pipeline,
    IGeofenceEvaluator geofenceEvaluator) : IPositioningSession
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

    async Task IPositioningSession.StopAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (!_isRunning)
                return;

            _isRunning = false;
        }

        await watchdog.StopAsync();
        await feed.StopAsync(ct);
    }

    private void OnPositionUpdated(object? sender, NormalizedPosition position) =>
        geofenceEvaluator.OnPositionUpdated(position);

    private async void OnSessionEndedUnexpectedly(object? sender, SessionEndReason reason)
    {
        lock (_sync)
        {
            if (!_isRunning)
                return;

            _isRunning = false;
        }

        await watchdog.StopAsync();
        await feed.StopAsync();

        _sessionEndedUnexpectedly?.Invoke(this, reason);
    }
}
