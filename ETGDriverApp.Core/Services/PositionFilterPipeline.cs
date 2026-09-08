using ETGDriverApp.Core.Models;
using ETGDriverApp.Core.Services.DeadReckoning;
using ETGDriverApp.Core.Services.Filters;

namespace ETGDriverApp.Core.Services;

public interface IPositionFilterPipeline
{
    event EventHandler<PositionEvaluatedEventArgs> PositionEvaluated;

    event EventHandler<NormalizedPosition> PositionUpdated;

    event EventHandler LocationUnavailable;

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
    private readonly IAccuracyGate _accuracyGate;
    private readonly IPlausibilityGate _plausibilityGate;
    private readonly IMapMatcher _mapMatcher;
    private readonly IPositionStateMachine _stateMachine;
    private readonly IDeadReckoningEstimator _deadReckoning;

    private readonly PositionKalmanFilter _filter = new();

    // three producers ingest concurrently: the location listener, the DR
    // timer, and forced fixes
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NormalizedPosition? _published;
    private DateTimeOffset _lastAcceptedTimestamp = DateTimeOffset.MinValue;
    private double _lastKnownUncertaintyMeters;

    private EventHandler<PositionEvaluatedEventArgs>? _positionEvaluated;
    private EventHandler<NormalizedPosition>? _positionUpdated;
    private EventHandler? _locationUnavailable;

    public PositionFilterPipeline(
        IAccuracyGate accuracyGate,
        IPlausibilityGate plausibilityGate,
        IMapMatcher mapMatcher,
        IPositionStateMachine stateMachine,
        IDeadReckoningEstimator deadReckoning)
    {
        _accuracyGate = accuracyGate;
        _plausibilityGate = plausibilityGate;
        _mapMatcher = mapMatcher;
        _stateMachine = stateMachine;
        _deadReckoning = deadReckoning;

        _stateMachine.LocationBecameUnavailable += OnLocationBecameUnavailable;
    }

    event EventHandler<PositionEvaluatedEventArgs> IPositionFilterPipeline.PositionEvaluated
    {
        add => _positionEvaluated += value;
        remove => _positionEvaluated -= value;
    }

    event EventHandler<NormalizedPosition> IPositionFilterPipeline.PositionUpdated
    {
        add => _positionUpdated += value;
        remove => _positionUpdated -= value;
    }

    event EventHandler IPositionFilterPipeline.LocationUnavailable
    {
        add => _locationUnavailable += value;
        remove => _locationUnavailable -= value;
    }

    NormalizedPosition? IPositionFilterPipeline.Current => Volatile.Read(ref _published);

    async Task<NormalizedPosition?> IPositionFilterPipeline.IngestAsync(
        RawPositionSample sample, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            // ordering: overlapping deliveries around a batched background
            // wake must not drag the track backwards
            if (sample.Timestamp < _lastAcceptedTimestamp)
            {
                RaiseEvaluated(sample, false, RejectionReason.OutOfOrderTimestamp);

                return null;
            }

            var tier = _accuracyGate.Classify(sample);

            if (tier == AccuracyTier.Rejected)
            {
                RaiseEvaluated(sample, false, RejectionReason.AccuracyBelowThreshold);

                return null;
            }

            if (!_filter.IsInitialized)
            {
                // a DR estimate cannot seed the filter; it has no anchor of
                // its own to offer
                if (sample.SourceType == PositionSourceType.DeadReckoned)
                    return null;

                _filter.Initialize(
                    sample.Latitude, sample.Longitude, sample.AccuracyMeters, sample.Timestamp);
            }
            else
            {
                _filter.Predict(sample.Timestamp);
                
                // DR is derived from the filter's own past output; feeding it
                // back while real fixes are arriving is circular
                if (sample.SourceType == PositionSourceType.DeadReckoned &&
                    _stateMachine.CurrentState != PositionState.DeadReckoning)
                    return null;

                if (!_plausibilityGate.Accepts(sample, _filter, out var diagnostics))
                {
                    RaiseEvaluated(sample, false, RejectionReason.ImplausibleJump, diagnostics);

                    return null;
                }

                _filter.UpdatePosition(sample.Latitude, sample.Longitude, sample.AccuracyMeters);

                if (sample.SourceType != PositionSourceType.DeadReckoned &&
                    sample is { SpeedMps: { } speed, CourseDegrees: { } course })
                    _filter.UpdateVelocity(speed, course, speedAccuracyMps: 1.0);
            }

            var (lat, lon) = _filter.Position;

            var filtered = new NormalizedPosition(
                lat, lon, sample.AccuracyMeters, sample.SourceType, sample.Timestamp,
                PositionState.Tracking, _filter.PositionUncertaintyMeters);

            var matched = await _mapMatcher.SnapToRoadAsync(filtered, ct);

            _stateMachine.NotifyFixAccepted(matched, tier);
            
            // the filter is the authoritative velocity source; DR only needs
            // an anchor and a speed to extrapolate from
            if (tier == AccuracyTier.Good && sample.SourceType != PositionSourceType.DeadReckoned)
                _deadReckoning.RecalibrateAgainst(matched, _filter.SpeedMps);

            var result = matched with
            {
                State = _stateMachine.CurrentState,
                UncertaintyRadiusMeters = _filter.PositionUncertaintyMeters
            };

            Volatile.Write(ref _published, result);
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
                Math.Max(position.EffectiveRadiusMeters, 1),
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

    private void OnLocationBecameUnavailable(object? sender, EventArgs e)
    {
        Volatile.Write(ref _published, null);
        _locationUnavailable?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseEvaluated(
        RawPositionSample sample, bool accepted, RejectionReason reason, string? diagnostics = null) =>
        _positionEvaluated?.Invoke(this, new PositionEvaluatedEventArgs(sample, accepted, reason, diagnostics));
}