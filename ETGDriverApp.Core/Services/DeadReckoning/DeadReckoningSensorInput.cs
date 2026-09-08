namespace ETGDriverApp.Core.Services.DeadReckoning;

// marker interface every pluggable sensor source implements
internal interface IDeadReckoningSensorInput
{
    void Start();
    void Stop();
}

// contributes heading-rate updates (degrees/sec, signed)
internal interface IHeadingRateProvider : IDeadReckoningSensorInput
{
    event EventHandler<double> HeadingRateChanged;
}

// contributes a coarse motion classification - optional; if nothing
// registers this, the estimator just falls back to held speed
internal interface IMotionStateProvider : IDeadReckoningSensorInput
{
    event EventHandler<MotionState> MotionStateChanged;
}

internal enum MotionState { Unknown, Stationary, Moving }
