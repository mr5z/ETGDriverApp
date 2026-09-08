using ETGDriverApp.Core.Models;

namespace ETGDriverApp.Core.Services;

// handles the app being killed and restarted mid-shift
internal interface ISessionRecovery
{
    Task<SessionRecoveryResult> RecoverAsync(CancellationToken ct = default);
}

internal record SessionRecoveryResult(
    IReadOnlyList<NormalizedPosition> RecoveredTrack,
    TimeSpan? GapDuration)
{
    public bool IsRestart => GapDuration is not null;
}

internal class SessionRecovery(
    IPositionStore store,
    IPositionStateMachine stateMachine,
    TimeProvider clock) : ISessionRecovery
{
    private static readonly TimeSpan MaxUsefulContinuity = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecoveryWindow = TimeSpan.FromHours(12);

    async Task<SessionRecoveryResult> ISessionRecovery.RecoverAsync(CancellationToken ct)
    {
        var latest = await store.GetLatestAsync(ct);

        if (latest is null)
            return new SessionRecoveryResult([], null);

        var now = clock.GetUtcNow();
        var gap = now - latest.Timestamp;
        var track = await store.GetSinceAsync(now - RecoveryWindow, ct);

        // the pipeline is deliberately not seeded with the last persisted
        // position: it would trip the speed checker on the first real fix
        if (gap > MaxUsefulContinuity)
            stateMachine.NotifyFixStale();

        return new SessionRecoveryResult(track, gap);
    }
}
