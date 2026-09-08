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
    
    // temporary; remove once DR speed is sorted
    public static event EventHandler<string>? Trace;
    
    private readonly IReadOnlyList<IDeadReckoningSensorInput> _sensors = [.. sensors];
    private readonly Lock _sync = new();

    private double _estimatedLatitude;
    private double _estimatedLongitude;
    private bool _hasAnchor;
    private double _speedMps;

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

                    // below the fix error, the bearing between two points is
                    // the direction of the noise
                    if (travelled > floor)
                    {
                        _headingOffsetDegrees = Geo.NormalizeDegrees(impliedHeading - _headingDegrees);
                        _speedMps = travelled / elapsed;
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
            if (sensor is IHeadingRateProvider headingRate)
                headingRate.HeadingRateChanged -= OnHeadingRateChanged;

            if (sensor is IMotionStateProvider motionState)
                motionState.MotionStateChanged -= OnMotionStateChanged;

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

            sample = new RawPositionSample(
                lat, lon, DeadReckonedAccuracyMeters, now, PositionSourceType.DeadReckoned,
                effectiveSpeed, correctedHeading);
            
            Trace?.Invoke(this,
                $"extrap heading={_headingDegrees:F1} offset={_headingOffsetDegrees:F1} " +
                $"corrected={correctedHeading:F1} speed={effectiveSpeed:F1}");
        }

        _estimateProduced?.Invoke(this, sample);
    }

    private static TimeSpan IntervalFor(DeadReckoningRate rate) =>
        rate == DeadReckoningRate.Full ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10);
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
