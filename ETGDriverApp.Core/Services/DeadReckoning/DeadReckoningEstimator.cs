using ETGDriverApp.Core.Models;

namespace ETGDriverApp.Core.Services.DeadReckoning;

internal interface IDeadReckoningEstimator : IAsyncDisposable
{
    void Start();

    void RecalibrateAgainst(NormalizedPosition trustedFix, double speedMps);

    // extrapolation runs only while active; an inactive tick leaves the
    // anchor untouched, so recalibration always compares against it
    void SetActive(bool active);

    event EventHandler<RawPositionSample> EstimateProduced;
}

// heading integration plus held speed
internal class HeadingIntegrationDeadReckoningEstimator(
    IEnumerable<IDeadReckoningSensorInput> sensors,
    IPeriodicScheduler scheduler,
    TimeProvider clock) : IDeadReckoningEstimator
{
    private const double BaseAccuracyMeters = 30;
    private const double HeadingDriftDegPerSec = 0.5;
    private const double SpeedErrorFraction = 0.1;
    private const double MinTrustworthyFixErrorMeters = 10;

    // integral gain for the gyro bias estimate, and a sanity cap on it
    private const double BiasGain = 0.1;
    private const double MaxBiasDegPerSec = 2;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    // offset changes over a longer span include outage catch-up, not just bias
    private static readonly TimeSpan MaxBiasSpan = TimeSpan.FromSeconds(30);

    // temporary; remove once DR speed is sorted
    public static event EventHandler<string>? Trace;

    private readonly IReadOnlyList<IDeadReckoningSensorInput> _sensors = [.. sensors];
    private readonly Lock _sync = new();

    private double _estimatedLatitude;
    private double _estimatedLongitude;
    private bool _hasAnchor;
    private double _speedMps;
    private DateTimeOffset _anchoredAt;

    // raw integrated device yaw, bias-corrected; never overwritten at recalibration
    private double _headingDegrees;

    // device-frame to vehicle-frame correction
    private double _headingOffsetDegrees;

    private double _gyroBiasDegPerSec;
    private DateTimeOffset? _lastOffsetUpdateAt;

    // error accumulated since the anchor; stopping adds none but removes none
    private double _accuracySinceAnchor = BaseAccuracyMeters;

    private DateTimeOffset _lastHeadingUpdate;
    private DateTimeOffset _lastExtrapolationAt;
    private MotionState _motionState = MotionState.Unknown;
    private bool _active;
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

                    var floor = Math.Max(trustedFix.EffectiveRadiusMeters, MinTrustworthyFixErrorMeters);

                    // Across an outage this bearing is a catch-up vector, not a heading: it
                    // points from where DR drifted to where the vehicle actually is. Bound it
                    // by how far the vehicle could plausibly have gone since the last estimate,
                    // using the incoming filter speed as well as the held one so the first
                    // anchored fix (when _speedMps is still 0) is not rejected.
                    var assumedSpeed = Math.Max(_speedMps, speedMps);
                    var reachable = assumedSpeed * elapsed + floor;

                    if (travelled > floor && travelled <= reachable)
                    {
                        UpdateOffsetAndBias(impliedHeading, trustedFix.Timestamp);

                        Trace?.Invoke(this,
                            $"recal elapsed={elapsed:F2} travelled={travelled:F1} floor={floor:F1} " +
                            $"speed={_speedMps:F1} bias={_gyroBiasDegPerSec:F2}");
                    }
                    else if (travelled <= floor)
                    {
                        // a stationary vehicle produces no usable bearing; not a rejection
                        Trace?.Invoke(this, $"recal: travelled={travelled:F1} below floor={floor:F1}");
                    }
                    else
                    {
                        Trace?.Invoke(this,
                            $"recal rejected: travelled={travelled:F1} reachable={reachable:F1}");
                    }
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
            _anchoredAt = trustedFix.Timestamp;
            _accuracySinceAnchor = BaseAccuracyMeters;
            _hasAnchor = true;
        }
    }

    void IDeadReckoningEstimator.SetActive(bool active)
    {
        lock (_sync)
            _active = active;
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

        scheduler.Start(TickInterval, _ =>
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

    // caller holds _sync
    private void UpdateOffsetAndBias(double impliedHeading, DateTimeOffset at)
    {
        var newOffset = Geo.NormalizeDegrees(impliedHeading - _headingDegrees);

        // the offset only moves if the corrected heading is still drifting;
        // feed that residual back into the bias until it stops
        if (_lastOffsetUpdateAt is { } previous &&
            at - previous is var span &&
            span > TimeSpan.Zero && span <= MaxBiasSpan)
        {
            var residual = -SignedDelta(_headingOffsetDegrees, newOffset) / span.TotalSeconds;

            _gyroBiasDegPerSec = Math.Clamp(
                _gyroBiasDegPerSec + BiasGain * residual, -MaxBiasDegPerSec, MaxBiasDegPerSec);
        }

        _headingOffsetDegrees = newOffset;
        _lastOffsetUpdateAt = at;
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

            _headingDegrees = Geo.NormalizeDegrees(
                _headingDegrees + (degPerSec - _gyroBiasDegPerSec) * dt);
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
            if (!_hasAnchor || !_active)
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

            // never shrinks until the next anchor: stopping mid-outage must not
            // make a drifted position look precise
            _accuracySinceAnchor = Math.Max(
                _accuracySinceAnchor, DriftAccuracyMeters(sinceAnchor, effectiveSpeed));

            sample = new RawPositionSample(
                lat, lon, _accuracySinceAnchor, now, PositionSourceType.DeadReckoned,
                effectiveSpeed, correctedHeading);

            Trace?.Invoke(this,
                $"extrap heading={_headingDegrees:F1} offset={_headingOffsetDegrees:F1} " +
                $"corrected={correctedHeading:F1} speed={effectiveSpeed:F1} " +
                $"since={sinceAnchor:F1} acc={_accuracySinceAnchor:F0} bias={_gyroBiasDegPerSec:F2}");
        }

        _estimateProduced?.Invoke(this, sample);
    }

    private static double SignedDelta(double from, double to) =>
        ((to - from + 540) % 360) - 180;

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

    // Degraded keeps DR warm so DeadReckoning continues from a live estimate.
    //
    // Deliberately NOT also driven by LocationBecameUnavailable: the state
    // machine raises that from inside the same call that transitions to
    // DeadReckoning, so listening to both would switch DR on and straight
    // back off before it ever ticked. Running past the unavailable point is
    // harmless now - the estimate carries its accumulated accuracy, and the
    // pipeline re-anchors on the next real fix rather than gating it.
    private void OnStateChanged(object? sender, PositionState state) =>
        estimator.SetActive(state is PositionState.Degraded or PositionState.DeadReckoning);

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