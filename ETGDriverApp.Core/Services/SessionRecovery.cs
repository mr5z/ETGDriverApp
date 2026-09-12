using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Models;
using Microsoft.Extensions.Options;

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
    IPositionFilterPipeline pipeline,
    TimeProvider clock,
    IOptionsMonitor<PositioningOptions> options) : ISessionRecovery
{
    async Task<SessionRecoveryResult> ISessionRecovery.RecoverAsync(CancellationToken ct)
    {
        var recovery = options.CurrentValue.Recovery;

        var latest = await store.GetLatestAsync(ct);

        if (latest is null)
            return new SessionRecoveryResult([], null);

        var now = clock.GetUtcNow();
        var gap = now - latest.Timestamp;
        var track = await store.GetSinceAsync(now - recovery.RecoveryWindow, ct);

        // the pipeline is deliberately not seeded with the last persisted
        // position: it would be an unbounded extrapolation from a cold start
        if (gap > recovery.MaxUsefulContinuity)
        {
            // was NotifyFixStale(double.MaxValue); the sentinel meant "there
            // is no filter state to report", which is now said directly
            stateMachine.NotifyFixLost();
        }
        else
        {
            // the filter widens from here rather than trusting it outright,
            // so a stale seed costs nothing the first real fix won't correct
            pipeline.SeedFrom(latest, gap);
        }

        return new SessionRecoveryResult(track, gap);
    }
}
