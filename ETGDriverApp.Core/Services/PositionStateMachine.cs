using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Models;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Services;

public interface IPositionStateMachine
{
    PositionState CurrentState { get; }

    event EventHandler<PositionState> StateChanged;

    event EventHandler LocationBecameUnavailable;

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
    private bool _unavailableRaisedForCurrentEpisode;
    private PositionState _currentState = PositionState.Tracking;

    PositionState IPositionStateMachine.CurrentState => _currentState;

    private EventHandler<PositionState>? _stateChanged;
    event EventHandler<PositionState> IPositionStateMachine.StateChanged
    {
        add => _stateChanged += value;
        remove => _stateChanged -= value;
    }

    private EventHandler? _locationBecameUnavailable;
    event EventHandler IPositionStateMachine.LocationBecameUnavailable
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

            _unavailableRaisedForCurrentEpisode = false;

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
        var maxUncertainty = snapshot.State.MaxUsefulUncertaintyMeters;
        var maxBlind = snapshot.State.MaxBlindDuration;

        PositionState? transitioned;
        var raiseUnavailable = false;

        lock (_sync)
        {
            var now = clock.GetUtcNow();

            // a real fix arrived after the watchdog decided we were stale
            if (!fixLost && _lastRealFixAt is { } recent && now - recent < staleAfter)
                return;

            _reacquiringSince = null;

            // on the tick that first enters DeadReckoning, the uncertainty
            // passed in is the filter's unaided prediction: its covariance
            // grows with the fourth power of the gap, so it clears the
            // threshold within seconds. DR has not fed the filter yet at
            // this point, so give it one watchdog interval to aid it.
            var wasAlreadyDeadReckoning = _currentState == PositionState.DeadReckoning;

            transitioned = SetState(PositionState.DeadReckoning);

            var blindFor = _lastRealFixAt is { } last
                ? now - last
                : TimeSpan.Zero;

            // A lost fix skips the grace period outright: there is no filter
            // state to grow, so waiting another tick proves nothing.
            var unusable = fixLost ||
                (wasAlreadyDeadReckoning &&
                 (uncertaintyRadiusMeters >= maxUncertainty || blindFor >= maxBlind));

            // a stationary vehicle keeps its uncertainty low and stays
            // usable; one at speed passes the threshold quickly
            if (unusable && !_unavailableRaisedForCurrentEpisode)
            {
                _unavailableRaisedForCurrentEpisode = true;
                raiseUnavailable = true;
            }
        }

        RaiseIfChanged(transitioned);

        if (raiseUnavailable)
            _locationBecameUnavailable?.Invoke(this, EventArgs.Empty);
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
}
