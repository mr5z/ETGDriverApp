using ETGDriverApp.Core.Models;
using ETGDriverApp.Core.Services.DeadReckoning;

namespace ETGDriverApp.Core.Services;

internal interface IPositionStateMachine
{
    PositionState CurrentState { get; }

    double? UncertaintyRadiusMeters { get; }

    event EventHandler<PositionState> StateChanged;

    event EventHandler LocationBecameUnavailable;

    void NotifyFixAccepted(NormalizedPosition fix, AccuracyTier tier);

    void NotifyFixStale();

    void NotifyEnteringKnownDeadZone();

    void NotifyForegroundResuming();
}

internal class PositionStateMachine(
    IDeadReckoningEstimator deadReckoning,
    TimeProvider clock) : IPositionStateMachine
{
    private static readonly TimeSpan MaxDeadReckoningDuration = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan ReacquisitionSettleDuration = TimeSpan.FromSeconds(5);

    private readonly Lock _sync = new();

    private DateTimeOffset? _deadReckoningStartedAt;
    private DateTimeOffset? _reacquiringSince;
    private bool _unavailableRaisedForCurrentEpisode;
    private PositionState _currentState = PositionState.Tracking;
    private double? _uncertaintyRadiusMeters;

    private EventHandler<PositionState>? _stateChanged;
    private EventHandler? _locationBecameUnavailable;

    PositionState IPositionStateMachine.CurrentState => _currentState;

    double? IPositionStateMachine.UncertaintyRadiusMeters => _uncertaintyRadiusMeters;

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
        PositionState? transitioned;
        bool shouldRecalibrate;

        lock (_sync)
        {
            // Degraded is a real but imprecise fix, not extrapolation
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
            
            shouldRecalibrate = tier == AccuracyTier.Good;

            _deadReckoningStartedAt = null;
            _unavailableRaisedForCurrentEpisode = false;

            _uncertaintyRadiusMeters = next switch
            {
                PositionState.Tracking => null,
                _ => fix.AccuracyMeters
            };

            transitioned = SetState(next);
        }

        if (shouldRecalibrate)
            deadReckoning.RecalibrateAgainst(fix);

        RaiseIfChanged(transitioned);
    }

    void IPositionStateMachine.NotifyFixStale()
    {
        PositionState? transitioned;
        var raiseUnavailable = false;

        lock (_sync)
        {
            _deadReckoningStartedAt ??= clock.GetUtcNow();
            _reacquiringSince = null;

            var elapsed = clock.GetUtcNow() - _deadReckoningStartedAt.Value;

            transitioned = SetState(PositionState.DeadReckoning);

            if (elapsed >= MaxDeadReckoningDuration)
            {
                _uncertaintyRadiusMeters = null;

                if (!_unavailableRaisedForCurrentEpisode)
                {
                    _unavailableRaisedForCurrentEpisode = true;
                    raiseUnavailable = true;
                }
            }
            else
            {
                _uncertaintyRadiusMeters = GrowUncertainty(elapsed);
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
        {
            _deadReckoningStartedAt ??= clock.GetUtcNow();
            transitioned = SetState(PositionState.DeadReckoning);
        }

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

    private static double GrowUncertainty(TimeSpan elapsed) => elapsed.TotalSeconds * 2.0;

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
