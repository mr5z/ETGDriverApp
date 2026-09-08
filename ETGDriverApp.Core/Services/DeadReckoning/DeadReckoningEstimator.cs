using ETGDriverApp.Core.Models;

namespace ETGDriverApp.Core.Services.DeadReckoning;

internal enum DeadReckoningRate { Low, Full }

internal interface IDeadReckoningEstimator : IAsyncDisposable
{
    void Start();
    
    void RecalibrateAgainst(NormalizedPosition trustedFix, double speedMps);

    void SetRate(DeadReckoningRate rate);

    event EventHandler<RawPositionSample> EstimateProduced;
}

// heading integration plus held speed
internal class HeadingIntegrationDeadReckoningEstimator(
    IEnumerable<IDeadReckoningSensorInput> sensors,
    IPeriodicScheduler scheduler,
    TimeProvider clock) : IDeadReckoningEstimator
{
    // Deliberately weak and constant: the filter grows uncertainty over time
    // through its own process noise, so a second drift model here would
    // compound with it.
    private const double DeadReckonedAccuracyMeters = 30;
    private const double BaseAccuracyMeters = 30;
    private const double HeadingDriftDegPerSec = 0.5;
    private const double SpeedErrorFraction = 0.1;
    
    // temporary; remove once DR speed is sorted
    public static event EventHandler<string>? Trace;
    
    private readonly IReadOnlyList<IDeadReckoningSensorInput> _sensors = [.. sensors];
    private readonly Lock _sync = new();

    private double _estimatedLatitude;
    private double _estimatedLongitude;
    private bool _hasAnchor;
    private double _speedMps;
    private DateTimeOffset _anchoredAt;

    // raw integrated device yaw; never overwritten at recalibration
    private double _headingDegrees;

    // device-frame to vehicle-frame correction
    private double _headingOffsetDegrees;

    private DateTimeOffset _lastHeadingUpdate;
    private DateTimeOffset _lastExtrapolationAt;
    private MotionState _motionState = MotionState.Unknown;
    private DeadReckoningRate _rate = DeadReckoningRate.Low;
    private bool _started;

    private EventHandler<RawPositionSample>? _estimateProduced;

    event EventHandler<RawPositionSample> IDeadReckoningEstimator.EstimateProduced
    {
        add => _estimateProduced += value;
        remove => _estimateProduced -= value;
    }

    void IDeadReckoningEstimator.RecalibrateAgainst(NormalizedPosition trustedFix, double speedMps)
    {
        lock (_sync)
        {
            if (_hasAnchor)
            {
                var elapsed = (trustedFix.Timestamp - _lastExtrapolationAt).TotalSeconds;

                if (elapsed > 0.5)
                {
                    var impliedHeading = Geo.BearingDegrees(
                        _estimatedLatitude, _estimatedLongitude,
                        trustedFix.Latitude, trustedFix.Longitude);

                    var travelled = Geo.DistanceMeters(
                        _estimatedLatitude, _estimatedLongitude,
                        trustedFix.Latitude, trustedFix.Longitude);
                    
                    var floor = Math.Max(trustedFix.EffectiveRadiusMeters, 10);

                    // across an outage this bearing is a catch-up vector, not a heading. DR
                    // extrapolated at _speedMps, so anything much beyond that in the elapsed
                    // time is the accumulated drift being closed, not distance travelled.
                    var plausible = _speedMps * elapsed + trustedFix.EffectiveRadiusMeters;

                    if (travelled > floor && travelled <= plausible)
                    {
                        _headingOffsetDegrees = Geo.NormalizeDegrees(impliedHeading - _headingDegrees);
                    }
                    
                    Trace?.Invoke(this,
                        $"recal elapsed={elapsed:F2} travelled={travelled:F1} floor={floor:F1} speed={_speedMps:F1}");
                }
                else
                {
                    Trace?.Invoke(this, $"recal skipped: elapsed={elapsed:F2}");
                }
            }
            else
            {
                Trace?.Invoke(this, "recal: no anchor yet");
            }

            _speedMps = speedMps;
            _estimatedLatitude = trustedFix.Latitude;
            _estimatedLongitude = trustedFix.Longitude;
            _lastExtrapolationAt = trustedFix.Timestamp;
            _hasAnchor = true;
        }
    }

    void IDeadReckoningEstimator.SetRate(DeadReckoningRate rate)
    {
        lock (_sync)
            _rate = rate;

        scheduler.SetInterval(IntervalFor(rate));
    }

    void IDeadReckoningEstimator.Start()
    {
        lock (_sync)
        {
            if (_started)
                return;

            _started = true;
        }

        // subscribe to whichever capabilities are registered
        foreach (var sensor in _sensors)
        {
            if (sensor is IHeadingRateProvider headingRate)
                headingRate.HeadingRateChanged += OnHeadingRateChanged;

            if (sensor is IMotionStateProvider motionState)
                motionState.MotionStateChanged += OnMotionStateChanged;

            sensor.Start();
        }

        scheduler.Start(IntervalFor(_rate), _ =>
        {
            Extrapolate();

            return Task.CompletedTask;
        });
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        lock (_sync)
        {
            if (!_started)
                return;

            _started = false;
        }

        foreach (var sensor in _sensors)
        {
            switch (sensor)
            {
                case IHeadingRateProvider headingRate:
                    headingRate.HeadingRateChanged -= OnHeadingRateChanged;
                    break;
                case IMotionStateProvider motionState:
                    motionState.MotionStateChanged -= OnMotionStateChanged;
                    break;
            }

            sensor.Stop();
        }

        await scheduler.StopAsync();
    }

    private void OnHeadingRateChanged(object? sender, double degPerSec)
    {
        lock (_sync)
        {
            var now = clock.GetUtcNow();
            var dt = _lastHeadingUpdate == default ? 0 : (now - _lastHeadingUpdate).TotalSeconds;

            _lastHeadingUpdate = now;

            if (dt <= 0)
                return;
            
            // a parked vehicle's gyro reads bias and noise, nothing else.
            // integrating it only accumulates heading error that no
            // recalibration can remove, because a stationary vehicle never
            // travels far enough to clear RecalibrateAgainst's floor.
            if (_motionState == MotionState.Stationary)
                return;

            _headingDegrees = Geo.NormalizeDegrees(_headingDegrees + degPerSec * dt);
        }
    }

    private void OnMotionStateChanged(object? sender, MotionState state)
    {
        lock (_sync)
            _motionState = state;
    }
    
    private void Extrapolate()
    {
        RawPositionSample sample;

        lock (_sync)
        {
            if (!_hasAnchor)
                return;

            var now = clock.GetUtcNow();
            var dt = (now - _lastExtrapolationAt).TotalSeconds;

            if (dt <= 0)
                return;

            var effectiveSpeed = _motionState == MotionState.Stationary ? 0 : _speedMps;
            var correctedHeading = Geo.NormalizeDegrees(_headingDegrees + _headingOffsetDegrees);

            // advance from the last estimate by one tick, so integrated turns
            // are preserved rather than flattened into a straight line
            var (lat, lon) = Geo.Project(
                _estimatedLatitude, _estimatedLongitude, correctedHeading, effectiveSpeed * dt);

            _estimatedLatitude = lat;
            _estimatedLongitude = lon;
            _lastExtrapolationAt = now;
            
            var sinceAnchor = (now - _anchoredAt).TotalSeconds;
            var accuracy = DriftAccuracyMeters(sinceAnchor, effectiveSpeed);

            sample = new RawPositionSample(
                lat, lon, DeadReckonedAccuracyMeters, now, PositionSourceType.DeadReckoned,
                effectiveSpeed, correctedHeading);
            
            sample = new RawPositionSample(
                lat, lon, accuracy, now, PositionSourceType.DeadReckoned,
                effectiveSpeed, correctedHeading);
            
            Trace?.Invoke(this,
                $"extrap heading={_headingDegrees:F1} offset={_headingOffsetDegrees:F1} " +
                $"corrected={correctedHeading:F1} speed={effectiveSpeed:F1}");
        }

        _estimateProduced?.Invoke(this, sample);
    }

    private static TimeSpan IntervalFor(DeadReckoningRate rate) =>
        rate == DeadReckoningRate.Full ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10);
    
    // The mean cross-track offset of a track rotating at HeadingDriftDegPerSec
    // is roughly half the final heading error times the distance covered.
    private static double DriftAccuracyMeters(double seconds, double speedMps)
    {
        if (seconds <= 0)
            return BaseAccuracyMeters;

        var headingErrorRad = Geo.ToRad(HeadingDriftDegPerSec * seconds);
        var crossTrack = speedMps * seconds * headingErrorRad / 2;
        var alongTrack = SpeedErrorFraction * speedMps * seconds;

        return Math.Sqrt(
            BaseAccuracyMeters * BaseAccuracyMeters +
            crossTrack * crossTrack +
            alongTrack * alongTrack);
    }
}

internal class DeadReckoningFeed(
    IDeadReckoningEstimator estimator,
    IPositionFilterPipeline pipeline,
    IPositionStateMachine stateMachine)
{
    private bool _started;

    public event EventHandler<Exception>? BridgeFaulted;

    public void Start()
    {
        if (_started)
            return;

        _started = true;

        estimator.EstimateProduced += OnEstimateProduced;
        stateMachine.StateChanged += OnStateChanged;

        estimator.Start();
    }

    private void OnStateChanged(object? sender, PositionState state) =>
        estimator.SetRate(
            state is PositionState.Degraded or PositionState.DeadReckoning
                ? DeadReckoningRate.Full
                : DeadReckoningRate.Low);

    private async void OnEstimateProduced(object? sender, RawPositionSample sample)
    {
        try
        {
            await pipeline.IngestAsync(sample);
        }
        catch (Exception ex)
        {
            BridgeFaulted?.Invoke(this, ex);
        }
    }
}
