using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Diagnostics;
using ETGDriverApp.Core.Helpers;
using ETGDriverApp.Core.Models;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Services.DeadReckoning;

internal interface IDeadReckoningEstimator : IAsyncDisposable
{
    void Start();

    Task StopAsync();

    void RecalibrateAgainst(NormalizedPosition trustedFix, double speedMps);

    // extrapolation runs only while active; an inactive tick leaves the
    // anchor untouched, so recalibration always compares against it
    void SetActive(bool active);

    event EventHandler<RawPositionSample> EstimateProduced;
}

// heading integration plus held speed
internal class HeadingIntegrationDeadReckoningEstimator(
    IOptionsMonitor<PositioningOptions> options,
    IEnumerable<IDeadReckoningSensorInput> sensors,
    IPeriodicScheduler scheduler,
    TimeProvider clock,
    IPositioningDiagnostics diagnostics) : IDeadReckoningEstimator
{
    private const string TraceCategory = "dr";

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

    // error accumulated since the anchor; stopping adds none but removes none.
    // Seeded on the first anchor, since the floor is configurable now.
    private double _accuracySinceAnchor;

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
        var dr = options.CurrentValue.DeadReckoning;

        lock (_sync)
        {
            if (_hasAnchor)
                TryUpdateHeadingFrom(trustedFix, speedMps, dr);
            else
                Trace("recal: no anchor yet");

            _speedMps = speedMps;
            _estimatedLatitude = trustedFix.Latitude;
            _estimatedLongitude = trustedFix.Longitude;
            _lastExtrapolationAt = trustedFix.Timestamp;
            _anchoredAt = trustedFix.Timestamp;
            _accuracySinceAnchor = dr.BaseAccuracyMeters;
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
            _accuracySinceAnchor = options.CurrentValue.DeadReckoning.BaseAccuracyMeters;
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

        scheduler.Start(options.CurrentValue.DeadReckoning.TickInterval, _ =>
        {
            Extrapolate();

            return Task.CompletedTask;
        });
    }

    Task IDeadReckoningEstimator.StopAsync() => StopCoreAsync();

    // Dispose is now just "stop and never start again"; the teardown itself
    // lives in StopCoreAsync so the end of a shift and the end of the process
    // cannot drift apart.
    async ValueTask IAsyncDisposable.DisposeAsync() => await StopCoreAsync();

    private async Task StopCoreAsync()
    {
        lock (_sync)
        {
            if (!_started)
                return;

            _started = false;

            // a stopped estimator must not be left armed: SetActive is driven
            // by PositionState, which keeps changing after the session ends
            _active = false;
        }

        foreach (var sensor in _sensors)
        {
            // don't convert to switch statement
            if (sensor is IHeadingRateProvider headingRate)
                headingRate.HeadingRateChanged -= OnHeadingRateChanged;

            if (sensor is IMotionStateProvider motionState)
                motionState.MotionStateChanged -= OnMotionStateChanged;

            sensor.Stop();
        }

        await scheduler.StopAsync();
    }

    // caller holds _sync.
    //
    // Extracted from the middle of RecalibrateAgainst, where it was four
    // levels of nesting whose only purpose at three of them was to pick a
    // different trace message. The guards now read as a sequence of reasons
    // to give up.
    private void TryUpdateHeadingFrom(
        NormalizedPosition trustedFix, double speedMps, DeadReckoningOptions dr)
    {
        var elapsed = trustedFix.Timestamp - _lastExtrapolationAt;

        if (elapsed <= dr.MinRecalibrationInterval)
        {
            Trace($"recal skipped: elapsed={elapsed.TotalSeconds:F2}s");

            return;
        }

        var travelled = Geo.DistanceMeters(
            _estimatedLatitude, _estimatedLongitude,
            trustedFix.Latitude, trustedFix.Longitude);

        var floor = Math.Max(trustedFix.EffectiveRadiusMeters, dr.MinTrustworthyFixErrorMeters);

        if (travelled <= floor)
        {
            // a stationary vehicle produces no usable bearing; not a rejection
            Trace($"recal: travelled={travelled:F1} below floor={floor:F1}");

            return;
        }

        // Across an outage this bearing is a catch-up vector, not a heading: it
        // points from where DR drifted to where the vehicle actually is. Bound it
        // by how far the vehicle could plausibly have gone since the last estimate,
        // using the incoming filter speed as well as the held one so the first
        // anchored fix (when _speedMps is still 0) is not rejected.
        var assumedSpeed = Math.Max(_speedMps, speedMps);
        var reachable = assumedSpeed * elapsed.TotalSeconds + floor;

        if (travelled > reachable)
        {
            Trace($"recal rejected: travelled={travelled:F1} reachable={reachable:F1}");

            return;
        }

        var impliedHeading = Geo.BearingDegrees(
            _estimatedLatitude, _estimatedLongitude,
            trustedFix.Latitude, trustedFix.Longitude);

        UpdateOffsetAndBias(impliedHeading, trustedFix.Timestamp, dr);

        Trace($"recal elapsed={elapsed.TotalSeconds:F2} travelled={travelled:F1} " +
              $"floor={floor:F1} speed={_speedMps:F1} bias={_gyroBiasDegPerSec:F2}");
    }

    // caller holds _sync
    private void UpdateOffsetAndBias(
        double impliedHeading, DateTimeOffset at, DeadReckoningOptions dr)
    {
        var newOffset = Geo.NormalizeDegrees(impliedHeading - _headingDegrees);

        // the offset only moves if the corrected heading is still drifting;
        // feed that residual back into the bias until it stops
        if (_lastOffsetUpdateAt is { } previous &&
            at - previous is var span &&
            span > TimeSpan.Zero && span <= dr.MaxBiasSpan)
        {
            var residual = -Geo.SignedDelta(_headingOffsetDegrees, newOffset) / span.TotalSeconds;

            _gyroBiasDegPerSec = Math.Clamp(
                _gyroBiasDegPerSec + dr.BiasGain * residual,
                -dr.MaxBiasDegPerSec, dr.MaxBiasDegPerSec);
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
            // travels far enough to clear the recalibration floor.
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
        var dr = options.CurrentValue.DeadReckoning;

        RawPositionSample sample;

        lock (_sync)
        {
            if (!_started || !_hasAnchor || !_active)
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
                _accuracySinceAnchor, DriftAccuracyMeters(sinceAnchor, effectiveSpeed, dr));

            sample = new RawPositionSample(
                lat, lon, _accuracySinceAnchor, now, PositionSourceType.DeadReckoned,
                effectiveSpeed, correctedHeading);

            Trace($"extrap heading={_headingDegrees:F1} offset={_headingOffsetDegrees:F1} " +
                  $"corrected={correctedHeading:F1} speed={effectiveSpeed:F1} " +
                  $"since={sinceAnchor:F1} acc={_accuracySinceAnchor:F0} bias={_gyroBiasDegPerSec:F2}");
        }

        _estimateProduced?.Invoke(this, sample);
    }

    // The message is only interpolated when something is listening, which the
    // old static Trace event could not do.
    private void Trace(string message)
    {
        if (diagnostics.IsEnabled)
            diagnostics.Trace(TraceCategory, message);
    }

    // The mean cross-track offset of a track rotating at HeadingDriftDegPerSec
    // is roughly half the final heading error times the distance covered.
    private static double DriftAccuracyMeters(
        double seconds, double speedMps, DeadReckoningOptions dr)
    {
        if (seconds <= 0)
            return dr.BaseAccuracyMeters;

        var headingErrorRad = Geo.ToRad(dr.HeadingDriftDegPerSec * seconds);
        var crossTrack = speedMps * seconds * headingErrorRad / 2;
        var alongTrack = dr.SpeedErrorFraction * speedMps * seconds;

        return Math.Sqrt(
            dr.BaseAccuracyMeters * dr.BaseAccuracyMeters +
            crossTrack * crossTrack +
            alongTrack * alongTrack);
    }
}

internal class DeadReckoningFeed(
    IDeadReckoningEstimator estimator,
    IPositionFilterPipeline pipeline,
    IPositionStateMachine stateMachine)
{
    private readonly Lock _sync = new();

    private bool _started;

    public event EventHandler<Exception>? BridgeFaulted;

    // ReSharper disable once InconsistentlySynchronizedField
    public bool IsRunning => _started;

    public void Start()
    {
        lock (_sync)
        {
            if (_started)
                return;

            _started = true;
        }

        estimator.EstimateProduced += OnEstimateProduced;
        stateMachine.StateChanged += OnStateChanged;

        estimator.Start();
    }

    // The counterpart Start never had. Without it the feed stayed wired to
    // the estimator and the state machine for the life of the process: after
    // a shift ended the sensors kept streaming, the estimator kept ticking,
    // and extrapolations were still being pushed into a pipeline nobody was
    // watching. PositioningSession owns the shift, so it owns this call.
    public async Task StopAsync()
    {
        lock (_sync)
        {
            if (!_started)
                return;

            _started = false;
        }

        estimator.EstimateProduced -= OnEstimateProduced;
        stateMachine.StateChanged -= OnStateChanged;

        estimator.SetActive(false);

        await estimator.StopAsync();
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