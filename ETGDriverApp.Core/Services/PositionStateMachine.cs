using ETGDriverApp.Core.Models;

namespace ETGDriverApp.Core.Services;

internal interface IPositionStateMachine
{
    PositionState CurrentState { get; }

    event EventHandler<PositionState> StateChanged;

    event EventHandler LocationBecameUnavailable;

    void NotifyFixAccepted(NormalizedPosition fix, AccuracyTier tier);

    // uncertainty comes from the filter; the state machine only decides
    // whether it has become too large to be useful
    void NotifyFixStale(double uncertaintyRadiusMeters);

    // TODO for future work
    void NotifyEnteringKnownDeadZone();

    // TODO for future work
    void NotifyForegroundResuming();
}

internal class PositionStateMachine(TimeProvider clock) : IPositionStateMachine
{
    // beyond this the position is too vague to act on: wider than any
    // pickup fence, so a geofence decision could not be defended
    private const double MaxUsefulUncertaintyMeters = 150;

    private static readonly TimeSpan ReacquisitionSettleDuration = TimeSpan.FromSeconds(5);

    // however confident the filter is, a position no real fix has touched in
    // this long cannot be defended
    private static readonly TimeSpan MaxBlindDuration = TimeSpan.FromSeconds(90);

    // matches the watchdog's HardThreshold. The watchdog reads its own copy
    // of the last fix time, so a fix can land between its check and the
    // NotifyFixStale call; this timestamp is the authoritative one.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(20);

    private readonly Lock _sync = new();

    private DateTimeOffset? _reacquiringSince;
    private DateTimeOffset? _lastRealFixAt;
    private bool _unavailableRaisedForCurrentEpisode;
    private PositionState _currentState = PositionState.Tracking;

    private EventHandler<PositionState>? _stateChanged;
    private EventHandler? _locationBecameUnavailable;

    PositionState IPositionStateMachine.CurrentState => _currentState;

    event EventHandler<PositionState> IPositionStateMachine.StateChanged
    {
        add => _stateChanged += value;
        remove => _stateChanged -= value;
    }

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

        PositionState? transitioned;

        lock (_sync)
        {
            var now = clock.GetUtcNow();

            var wasExtrapolating = _currentState is PositionState.DeadReckoning;

            var stillSettling =
                _currentState == PositionState.Reacquiring &&
                _reacquiringSince is { } since &&
                now - since < ReacquisitionSettleDuration;

            var next = (tier, wasExtrapolating, stillSettling) switch
            {
                (_, true, _) => PositionState.Reacquiring,
                (_, _, true) => PositionState.Reacquiring,
                (AccuracyTier.Borderline, _, _) => PositionState.Degraded,
                _ => PositionState.Tracking
            };

            if (next == PositionState.Reacquiring && _currentState != PositionState.Reacquiring)
                _reacquiringSince = now;
            else if (next != PositionState.Reacquiring)
                _reacquiringSince = null;

            _unavailableRaisedForCurrentEpisode = false;

            RecordObservation(fix, now);

            transitioned = SetState(next);
        }

        RaiseIfChanged(transitioned);
    }

    void IPositionStateMachine.NotifyFixStale(double uncertaintyRadiusMeters)
    {
        PositionState? transitioned;
        var raiseUnavailable = false;

        lock (_sync)
        {
            var now = clock.GetUtcNow();

            // a real fix arrived after the watchdog decided we were stale
            if (_lastRealFixAt is { } recent && now - recent < StaleAfter)
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

            var unusable =
                wasAlreadyDeadReckoning &&
                (uncertaintyRadiusMeters >= MaxUsefulUncertaintyMeters ||
                 blindFor >= MaxBlindDuration);

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