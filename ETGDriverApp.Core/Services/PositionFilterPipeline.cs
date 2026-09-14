using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Models;
using ETGDriverApp.Core.Services.DeadReckoning;
using ETGDriverApp.Core.Services.Filters;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Services;

public interface IPositionFilterPipeline
{
    event EventHandler<PositionEvaluatedEventArgs> PositionEvaluated;

    event EventHandler<NormalizedPosition> PositionUpdated;

    event EventHandler<PositionUnusableEventArgs> LocationUnavailable;

    // returns the position this sample produced, or null if it was rejected
    Task<NormalizedPosition?> IngestAsync(RawPositionSample sample, CancellationToken ct = default);

    // advances the filter to now and returns its current uncertainty.
    // Predicting is a real state change: the covariance grows.
    double PredictUncertaintyMeters(DateTimeOffset now);

    // seeds the filter from a persisted position after a restart; the gap
    // widens the covariance so the first real fix dominates
    void SeedFrom(NormalizedPosition position, TimeSpan gap);

    NormalizedPosition? Current { get; }
}

public record PositionEvaluatedEventArgs(
    RawPositionSample Sample,
    bool Accepted,
    RejectionReason Reason,
    string? Diagnostics = null);

internal class PositionFilterPipeline : IPositionFilterPipeline, IDisposable
{
    // floor on the accuracy a persisted position is re-seeded with
    private const double MinSeedAccuracyMeters = 1;

    private readonly IAccuracyGate _accuracyGate;
    private readonly IAdmissionGate _admissionGate;
    private readonly IMapMatcher _mapMatcher;
    private readonly IPositionStateMachine _stateMachine;
    private readonly IDeadReckoningEstimator _deadReckoning;
    private readonly IOptionsMonitor<PositioningOptions> _options;

    private readonly PositionKalmanFilter _filter;

    // three producers ingest concurrently: the location listener, the DR
    // timer, and forced fixes
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NormalizedPosition? _published;
    private DateTimeOffset _lastAcceptedTimestamp = DateTimeOffset.MinValue;
    private double _lastKnownUncertaintyMeters;

    public PositionFilterPipeline(
        IAccuracyGate accuracyGate,
        IAdmissionGate admissionGate,
        IMapMatcher mapMatcher,
        IPositionStateMachine stateMachine,
        IDeadReckoningEstimator deadReckoning,
        IOptionsMonitor<PositioningOptions> options)
    {
        _accuracyGate = accuracyGate;
        _admissionGate = admissionGate;
        _mapMatcher = mapMatcher;
        _stateMachine = stateMachine;
        _deadReckoning = deadReckoning;
        _options = options;
        _filter = new PositionKalmanFilter(options);

        _stateMachine.LocationBecameUnavailable += OnLocationBecameUnavailable;
    }

    private EventHandler<PositionEvaluatedEventArgs>? _positionEvaluated;
    event EventHandler<PositionEvaluatedEventArgs> IPositionFilterPipeline.PositionEvaluated
    {
        add => _positionEvaluated += value;
        remove => _positionEvaluated -= value;
    }

    private EventHandler<NormalizedPosition>? _positionUpdated;
    event EventHandler<NormalizedPosition> IPositionFilterPipeline.PositionUpdated
    {
        add => _positionUpdated += value;
        remove => _positionUpdated -= value;
    }

    private EventHandler<PositionUnusableEventArgs>? _locationUnavailable;
    event EventHandler<PositionUnusableEventArgs> IPositionFilterPipeline.LocationUnavailable
    {
        add => _locationUnavailable += value;
        remove => _locationUnavailable -= value;
    }

    NormalizedPosition? IPositionFilterPipeline.Current => Volatile.Read(ref _published);

    async Task<NormalizedPosition?> IPositionFilterPipeline.IngestAsync(
        RawPositionSample sample, CancellationToken ct)
    {
        var filterOptions = _options.CurrentValue.Filter;

        await _gate.WaitAsync(ct);

        try
        {
            // Ordering: overlapping deliveries around a batched background
            // wake must not drag the track backwards.
            //
            // `<=`, not `<`. A repeated cached fix carries the same timestamp
            // every time the platform hands it back, and a strict comparison
            // let the identical sample through on every tick.
            if (!sample.IsDeadReckoned && sample.Timestamp <= _lastAcceptedTimestamp)
            {
                RaiseEvaluated(sample, false, RejectionReason.OutOfOrderTimestamp);

                return null;
            }

            // DR is derived from the filter's own past output; feeding it back while
            // real fixes are arriving is circular. Discard before touching the filter:
            // Predict is a state change, and the covariance growth it produces depends
            // on the step size, so predicting on samples we reject makes uncertainty a
            // function of the DR tick rate.
            var extrapolating = _stateMachine.CurrentState == PositionState.DeadReckoning;

            if (sample.IsDeadReckoned && !extrapolating)
                return null;

            var tier = _accuracyGate.Classify(sample);

            if (tier == AccuracyTier.Rejected)
            {
                RaiseEvaluated(sample, false, RejectionReason.AccuracyBelowThreshold);

                return null;
            }

            // Predict before the gate so the innovation frame has a current
            // prediction to judge against. Safe now that samples the pipeline
            // discards outright have already returned above.
            if (_filter.IsInitialized)
                _filter.Predict(sample.Timestamp);

            var verdict = _admissionGate.Evaluate(
                new AdmissionContext(sample, _filter, Volatile.Read(ref _published), extrapolating));

            if (!verdict.Accepted)
            {
                RaiseEvaluated(sample, false, verdict.Reason, verdict.Diagnostics);

                return null;
            }

            if (verdict.Effect == AdmissionEffect.Reseed)
            {
                _filter.Initialize(
                    sample.Latitude, sample.Longitude, sample.AccuracyMeters, sample.Timestamp);
            }
            else if (sample.IsDeadReckoned)
            {
                // Adopt the extrapolated POSITION - it follows turns the
                // constant-velocity prediction cannot - but leave the
                // covariance to grow. A DR sample is this filter's own past
                // output plus gyro integration; treating it as a measurement
                // let the filter confirm its own belief and collapse its
                // uncertainty on no evidence.
                _filter.SetPositionFromExtrapolation(sample.Latitude, sample.Longitude);

                // The stationary update stays. Motion state comes from an
                // accelerometer the filter has never touched, so a detected
                // stop is genuinely new, and it is the only thing that can
                // overrule a coasting velocity.
                ApplyVelocityUpdate(sample, filterOptions);
            }
            else
            {
                _filter.UpdatePosition(sample.Latitude, sample.Longitude, sample.AccuracyMeters);

                ApplyVelocityUpdate(sample, filterOptions);
            }

            var (lat, lon) = _filter.Position;

            var filtered = new NormalizedPosition(
                lat, lon, sample.AccuracyMeters, sample.SourceType, sample.Timestamp,
                PositionState.Tracking, _filter.PositionUncertaintyMeters);

            var matched = await _mapMatcher.SnapToRoadAsync(filtered, ct);

            _stateMachine.NotifyFixAccepted(matched, tier);

            // The filter is the authoritative velocity source; DR only needs
            // an anchor and a speed to extrapolate from.
            //
            // `IsObserved` rather than `!IsDeadReckoned`: a cached fix is not
            // an observation of now, so anchoring DR to it would restart the
            // drift budget from a position the vehicle has already left.
            if (tier == AccuracyTier.Good && sample.IsObserved)
                _deadReckoning.RecalibrateAgainst(matched, _filter.SpeedMps);

            // Both are read AFTER NotifyFixAccepted, so an accepted fix that
            // just restored defensibility is published as defensible rather
            // than carrying the previous tick's verdict.
            var result = matched with
            {
                State = _stateMachine.CurrentState,
                UncertaintyRadiusMeters = _filter.PositionUncertaintyMeters,
                IsDefensible = _stateMachine.IsDefensible
            };

            Volatile.Write(ref _published, result);

            if (!sample.IsDeadReckoned)
                _lastAcceptedTimestamp = sample.Timestamp;

            RaiseEvaluated(sample, true, RejectionReason.None);
            _positionUpdated?.Invoke(this, result);

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    double IPositionFilterPipeline.PredictUncertaintyMeters(DateTimeOffset now)
    {
        // the watchdog must not block behind an in-flight ingest, and a
        // missed prediction is recovered on the next tick
        if (!_gate.Wait(0))
            return _lastKnownUncertaintyMeters;

        try
        {
            if (!_filter.IsInitialized)
                return 0;

            _filter.Predict(now);

            _lastKnownUncertaintyMeters = _filter.PositionUncertaintyMeters;

            return _lastKnownUncertaintyMeters;
        }
        finally
        {
            _gate.Release();
        }
    }

    void IPositionFilterPipeline.SeedFrom(NormalizedPosition position, TimeSpan gap)
    {
        _gate.Wait();

        try
        {
            if (_filter.IsInitialized)
                return;

            _filter.Initialize(
                position.Latitude, position.Longitude,
                Math.Max(position.EffectiveRadiusMeters, MinSeedAccuracyMeters),
                position.Timestamp);

            // advance to now so the covariance reflects the elapsed gap
            _filter.Predict(position.Timestamp + gap);

            _lastKnownUncertaintyMeters = _filter.PositionUncertaintyMeters;
            _lastAcceptedTimestamp = position.Timestamp;
        }
        finally
        {
            _gate.Release();
        }
    }

    void IDisposable.Dispose()
    {
        _stateMachine.LocationBecameUnavailable -= OnLocationBecameUnavailable;
        _gate.Dispose();
    }

    private void OnLocationBecameUnavailable(object? sender, PositionUnusableEventArgs e) =>
        _locationUnavailable?.Invoke(this, e);

    // caller holds _gate.
    //
    // Two sources of velocity information, and the distinction matters.
    //
    // An OBSERVED fix carries the platform's own speed and course, measured
    // independently of anything we computed. It updates velocity normally.
    //
    // A DEAD-RECKONED sample's speed is one the filter handed to DR at the
    // last recalibration, so feeding it back is circular - it would let the
    // filter confirm its own belief and the velocity would never decay.
    //
    // With ONE exception, which is the whole reason this method exists. When
    // DR reports exactly zero, that is not the held speed echoing back: the
    // estimator zeroes it because the accelerometer says the vehicle is
    // stationary. Motion state comes from a sensor the filter has never
    // touched, so a stop is genuinely new evidence.
    //
    // Without this, a vehicle that stops mid-outage keeps being published as
    // moving. A captured run shows it plainly: DR reporting speed=0.0 with
    // its own estimate frozen, while the published track carried on at 12m/s
    // for fifty seconds and the error grew from 642m to 844m. The DR sample
    // does update position, but it arrives claiming thousands of metres of
    // accuracy, far too weak to overcome the coasting velocity state. The
    // velocity itself has to be corrected, and only a zero-velocity update
    // can do it.
    //
    // Non-zero DR speeds stay excluded. This is the standard zero-velocity
    // update of inertial navigation, not a general trust in DR's kinematics.
    private void ApplyVelocityUpdate(RawPositionSample sample, KalmanFilterOptions filter)
    {
        if (sample is not { SpeedMps: { } speed, CourseDegrees: { } course })
            return;

        if (sample.IsObserved)
        {
            _filter.UpdateVelocity(speed, course, filter.ReportedSpeedAccuracyMps);
            return;
        }

        if (sample.IsDeadReckoned && speed == 0)
        {
            _filter.DecouplePositionFromVelocity();
            _filter.UpdateVelocity(0, course, filter.StationaryUpdateAccuracyMps);
        }
    }

    private void RaiseEvaluated(
        RawPositionSample sample, bool accepted, RejectionReason reason, string? diagnostics = null) =>
        _positionEvaluated?.Invoke(
            this, new PositionEvaluatedEventArgs(sample, accepted, reason, diagnostics));
}