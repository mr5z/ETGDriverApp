using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Models;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Services;

public record PositionUnusableEventArgs(UnusableReason Reason, DateTimeOffset At);

public interface IPositionStateMachine
{
    PositionState CurrentState { get; }

    // False from the moment a give-up threshold is crossed until the next
    // accepted fix. Read at publish time so it travels with the position
    // instead of only existing as a notification nobody is obliged to hear.
    bool IsDefensible { get; }

    event EventHandler<PositionState> StateChanged;

    event EventHandler<PositionUnusableEventArgs> LocationBecameUnavailable;

    void NotifyFixAccepted(NormalizedPosition fix, AccuracyTier tier);

    // uncertainty comes from the filter; the state machine only decides
    // whether it has become too large to be useful
    void NotifyFixStale(double uncertaintyRadiusMeters);

    // The position is not merely uncertain, it is gone: no filter state to
    // report on. Replaces callers passing NotifyFixStale(double.MaxValue),
    // where a magic argument value carried the real meaning.
    void NotifyFixLost();

    // TODO for future work
    void NotifyEnteringKnownDeadZone();

    // TODO for future work
    void NotifyForegroundResuming();
}

internal class PositionStateMachine(
    TimeProvider clock,
    IOptionsMonitor<PositioningOptions> options) : IPositionStateMachine
{
    private readonly Lock _sync = new();

    private DateTimeOffset? _reacquiringSince;
    private DateTimeOffset? _lastRealFixAt;

    // the reason already announced for this episode. Latching on the reason
    // rather than on a bare bool: blind-too-long and uncertainty-too-large
    // are different facts with different remedies, and whichever crossed
    // first used to silence the other for the rest of the outage.
    private UnusableReason _announcedReason = UnusableReason.None;

    private PositionState _currentState = PositionState.NoFix;
    private bool _isDefensible = true;

    PositionState IPositionStateMachine.CurrentState => _currentState;

    bool IPositionStateMachine.IsDefensible => _isDefensible;

    private EventHandler<PositionState>? _stateChanged;
    event EventHandler<PositionState> IPositionStateMachine.StateChanged
    {
        add => _stateChanged += value;
        remove => _stateChanged -= value;
    }

    private EventHandler<PositionUnusableEventArgs>? _locationBecameUnavailable;
    event EventHandler<PositionUnusableEventArgs> IPositionStateMachine.LocationBecameUnavailable
    {
        add => _locationBecameUnavailable += value;
        remove => _locationBecameUnavailable -= value;
    }

    void IPositionStateMachine.NotifyFixAccepted(NormalizedPosition fix, AccuracyTier tier)
    {
        // an extrapolation is not evidence the outage ended
        if (fix.SourceType == PositionSourceType.DeadReckoned)
            return;

        // one snapshot per notification
        var settle = options.CurrentValue.State.ReacquisitionSettleDuration;

        PositionState? transitioned;

        lock (_sync)
        {
            var now = clock.GetUtcNow();

            var wasExtrapolating = _currentState is PositionState.DeadReckoning;

            var stillSettling =
                _currentState == PositionState.Reacquiring &&
                _reacquiringSince is { } since &&
                now - since < settle;

            // The old three-way tuple switch had two arms producing the same
            // state; the real rule is simply "any recovery in progress wins
            // over the accuracy tier".
            var recovering = wasExtrapolating || stillSettling;

            var next = recovering
                ? PositionState.Reacquiring
                : tier == AccuracyTier.Borderline
                    ? PositionState.Degraded
                    : PositionState.Tracking;

            if (next == PositionState.Reacquiring)
            {
                // only start the settle clock on the way in
                _reacquiringSince ??= now;
            }
            else
            {
                _reacquiringSince = null;
            }

            _announcedReason = UnusableReason.None;
            _isDefensible = true;

            RecordObservation(fix, now);

            transitioned = SetState(next);
        }

        RaiseIfChanged(transitioned);
    }

    void IPositionStateMachine.NotifyFixStale(double uncertaintyRadiusMeters) =>
        HandleStale(uncertaintyRadiusMeters, fixLost: false);

    void IPositionStateMachine.NotifyFixLost() =>
        HandleStale(uncertaintyRadiusMeters: 0, fixLost: true);

    void IPositionStateMachine.NotifyEnteringKnownDeadZone()
    {
        PositionState? transitioned;

        lock (_sync)
            transitioned = SetState(PositionState.DeadReckoning);

        RaiseIfChanged(transitioned);
    }

    void IPositionStateMachine.NotifyForegroundResuming()
    {
        PositionState? transitioned;

        lock (_sync)
        {
            _reacquiringSince = clock.GetUtcNow();
            transitioned = SetState(PositionState.Reacquiring);
        }

        RaiseIfChanged(transitioned);
    }

    private void HandleStale(double uncertaintyRadiusMeters, bool fixLost)
    {
        var snapshot = options.CurrentValue;

        // The watchdog reads its own copy of the last fix time, so a fix can
        // land between its check and this call; this timestamp is the
        // authoritative one. Both sides now read the same configured value
        // instead of keeping hand-synchronised private copies.
        var staleAfter = snapshot.Staleness.HardThreshold;

        PositionState? transitioned;
        DateTimeOffset now;
        var announce = UnusableReason.None;

        lock (_sync)
        {
            now = clock.GetUtcNow();

            // a real fix arrived after the watchdog decided we were stale
            if (!fixLost && _lastRealFixAt is { } recent && now - recent < staleAfter)
                return;

            var verdict = Judge(
                _lastRealFixAt, now, _currentState,
                uncertaintyRadiusMeters, fixLost, snapshot.State);

            _reacquiringSince = null;

            transitioned = SetState(verdict.State);

            if (verdict.Reason != UnusableReason.None)
            {
                _isDefensible = false;

                // announce each distinct reason once per episode, so crossing
                // the second threshold is still audible after the first
                if (verdict.Reason != _announcedReason)
                {
                    _announcedReason = verdict.Reason;
                    announce = verdict.Reason;
                }
            }
        }

        RaiseIfChanged(transitioned);

        if (announce != UnusableReason.None)
            _locationBecameUnavailable?.Invoke(
                this, new PositionUnusableEventArgs(announce, now));
    }

    // returns the new state only if it changed, so events are raised
    // outside the lock
    private PositionState? SetState(PositionState next)
    {
        if (next == _currentState)
            return null;

        _currentState = next;

        return next;
    }

    private void RaiseIfChanged(PositionState? transitioned)
    {
        if (transitioned is { } state)
            _stateChanged?.Invoke(this, state);
    }

    // When the world was last actually observed, which is the fix's own
    // timestamp rather than the moment we processed it: a background wake
    // delivers a batch of minutes-old fixes at once, and treating those as
    // fresh would reset the blind timer exactly when it should be firing.
    private void RecordObservation(NormalizedPosition fix, DateTimeOffset now)
    {
        // a device clock running ahead would otherwise suppress the guard
        var observedAt = fix.Timestamp > now ? now : fix.Timestamp;

        // within a stale batch, fixes can arrive out of order; an older one
        // must not drag the timer back behind a newer one already recorded
        if (_lastRealFixAt is { } previous && previous > observedAt)
            return;

        _lastRealFixAt = observedAt;
    }

    private readonly record struct StaleVerdict(PositionState State, UnusableReason Reason);

    private static StaleVerdict Judge(
        DateTimeOffset? lastRealFixAt,
        DateTimeOffset now,
        PositionState current,
        double uncertaintyRadiusMeters,
        bool fixLost,
        PositionStateOptions state)
    {
        // no anchor means nothing to extrapolate from, and nothing whose
        // uncertainty could grow - so the usual give-up tests can never fire
        if (lastRealFixAt is not { } last)
            return new StaleVerdict(PositionState.NoFix, UnusableReason.NoAnchor);

        if (fixLost)
            return new StaleVerdict(PositionState.DeadReckoning, UnusableReason.FixLost);

        // the first tick into DeadReckoning sees the filter's unaided
        // prediction; DR has not aided it yet, so allow one interval
        if (current != PositionState.DeadReckoning)
            return new StaleVerdict(PositionState.DeadReckoning, UnusableReason.None);

        // Blind time is checked first on purpose. It is the reason that does
        // not depend on a model being calibrated, so when both hold it is the
        // one worth naming.
        if (now - last >= state.MaxBlindDuration)
            return new StaleVerdict(PositionState.DeadReckoning, UnusableReason.BlindTooLong);

        if (uncertaintyRadiusMeters >= state.MaxUsefulUncertaintyMeters)
            return new StaleVerdict(
                PositionState.DeadReckoning, UnusableReason.UncertaintyTooLarge);

        return new StaleVerdict(PositionState.DeadReckoning, UnusableReason.None);
    }
}