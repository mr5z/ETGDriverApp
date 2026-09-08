using ETGDriverApp.Core.Models;
using ETGDriverApp.Core.Services.DeadReckoning;

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

    void NotifyEnteringKnownDeadZone();

    void NotifyForegroundResuming();
}

internal class PositionStateMachine(TimeProvider clock) : IPositionStateMachine
{
    // beyond this the position is too vague to act on: wider than any
    // pickup fence, so a geofence decision could not be defended
    private const double MaxUsefulUncertaintyMeters = 150;

    private static readonly TimeSpan ReacquisitionSettleDuration = TimeSpan.FromSeconds(5);

    private readonly Lock _sync = new();

    private DateTimeOffset? _reacquiringSince;
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
            var wasExtrapolating = _currentState is PositionState.DeadReckoning;

            var stillSettling =
                _currentState == PositionState.Reacquiring &&
                _reacquiringSince is { } since &&
                clock.GetUtcNow() - since < ReacquisitionSettleDuration;

            var next = (tier, wasExtrapolating, stillSettling) switch
            {
                (_, true, _) => PositionState.Reacquiring,
                (_, _, true) => PositionState.Reacquiring,
                (AccuracyTier.Borderline, _, _) => PositionState.Degraded,
                _ => PositionState.Tracking
            };

            if (next == PositionState.Reacquiring && _currentState != PositionState.Reacquiring)
                _reacquiringSince = clock.GetUtcNow();
            else if (next != PositionState.Reacquiring)
                _reacquiringSince = null;

            _unavailableRaisedForCurrentEpisode = false;

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
            _reacquiringSince = null;

            transitioned = SetState(PositionState.DeadReckoning);

            // a stationary vehicle keeps its uncertainty low and stays
            // usable; one at speed passes the threshold quickly
            if (uncertaintyRadiusMeters >= MaxUsefulUncertaintyMeters &&
                !_unavailableRaisedForCurrentEpisode)
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
}