using ETGDriverApp.Core.Models;

namespace ETGDriverApp.Core.Services;

public interface IPositionFilterPipeline
{
    event EventHandler<PositionEvaluatedEventArgs> PositionEvaluated;

    event EventHandler<NormalizedPosition> PositionUpdated;

    event EventHandler LocationUnavailable;

    // returns the position this sample produced, or null if it was rejected
    // or was a DR estimate that is not currently the output
    Task<NormalizedPosition?> IngestAsync(RawPositionSample sample, CancellationToken ct = default);

    NormalizedPosition? Current { get; }
}

public record PositionEvaluatedEventArgs(
    RawPositionSample Sample,
    bool Accepted,
    RejectionReason Reason,
    string? Diagnostics = null);

internal class PositionFilterPipeline : IPositionFilterPipeline, IDisposable
{
    private static readonly TimeSpan MaxBlendableAge = TimeSpan.FromSeconds(10);
    
    private readonly IAccuracyGate _accuracyGate;
    private readonly ISpeedSanityChecker _speedChecker;
    private readonly IPositionSmoother _smoother;
    private readonly IMapMatcher _mapMatcher;
    private readonly IPositionBlender _blender;
    private readonly IPositionStateMachine _stateMachine;

    // three producers ingest concurrently: the location listener, the DR
    // timer, and forced fixes
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NormalizedPosition? _published;
    private NormalizedPosition? _lastDeadReckoned;
    private NormalizedPosition? _lastRealFix;
    private DateTimeOffset _lastAcceptedTimestamp = DateTimeOffset.MinValue;

    private EventHandler<PositionEvaluatedEventArgs>? _positionEvaluated;
    private EventHandler<NormalizedPosition>? _positionUpdated;
    private EventHandler? _locationUnavailable;

    public PositionFilterPipeline(
        IAccuracyGate accuracyGate,
        ISpeedSanityChecker speedChecker,
        IPositionSmoother smoother,
        IMapMatcher mapMatcher,
        IPositionBlender blender,
        IPositionStateMachine stateMachine)
    {
        _accuracyGate = accuracyGate;
        _speedChecker = speedChecker;
        _smoother = smoother;
        _mapMatcher = mapMatcher;
        _blender = blender;
        _stateMachine = stateMachine;

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
            if (sample.SourceType == PositionSourceType.DeadReckoned)
                return await IngestDeadReckonedAsync(sample, ct);

            return await IngestRealFixAsync(sample, ct);
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

    private async Task<NormalizedPosition?> IngestRealFixAsync(RawPositionSample sample, CancellationToken ct)
    {
        // ordering: overlapping deliveries around a batched background wake
        // must not drag the track backwards
        if (sample.Timestamp < _lastAcceptedTimestamp)
        {
            RaiseEvaluated(sample, accepted: false, RejectionReason.OutOfOrderTimestamp);

            return null;
        }

        var tier = _accuracyGate.Classify(sample);

        if (tier == AccuracyTier.Rejected)
        {
            RaiseEvaluated(sample, accepted: false, RejectionReason.AccuracyBelowThreshold);

            return null;
        }
        
        var implied = _lastRealFix is null ? null : _speedChecker.ImpliedKph(sample, _lastRealFix);

        if (implied > _speedChecker.MaxPlausibleSpeedKph)
        {
            RaiseEvaluated(sample, false, RejectionReason.ImplausibleJump,
                $"kph={implied:F0} anchor={_lastRealFix!.Latitude:F5},{_lastRealFix.Longitude:F5} " +
                $"src={_lastRealFix.SourceType} dt={(sample.Timestamp - _lastRealFix.Timestamp).TotalSeconds:F2}");

            return null;
        }
        
        if (_lastRealFix is not null && !_speedChecker.Accepts(sample, _lastRealFix))
        {
            RaiseEvaluated(sample, accepted: false, RejectionReason.ImplausibleJump);

            return null;
        }

        var smoothed = _smoother.Smooth(sample, _lastRealFix);
        var matched = await _mapMatcher.SnapToRoadAsync(smoothed, ct);

        _stateMachine.NotifyFixAccepted(matched, tier);

        var state = _stateMachine.CurrentState;

        // a DR estimate that has been free-running is worse than no estimate
        var blendable = _lastDeadReckoned is { } dr &&
                        matched.Timestamp - dr.Timestamp <= MaxBlendableAge
            ? dr
            : null;

        var final = state is PositionState.Degraded or PositionState.Reacquiring && blendable is not null
            ? _blender.Blend(matched, blendable, gpsWeight: state == PositionState.Reacquiring ? 0.7 : 0.5)
            : matched;

        var result = final with
        {
            State = state,
            UncertaintyRadiusMeters = _stateMachine.UncertaintyRadiusMeters
        };

        Volatile.Write(ref _published, result);
        
        _lastRealFix = matched;
        _lastAcceptedTimestamp = sample.Timestamp;

        RaiseEvaluated(sample, accepted: true, RejectionReason.None);
        _positionUpdated?.Invoke(this, result);

        return result;
    }

    private async Task<NormalizedPosition?> IngestDeadReckonedAsync(RawPositionSample sample, CancellationToken ct)
    {
        var smoothed = _smoother.Smooth(sample, _published);
        var matched = await _mapMatcher.SnapToRoadAsync(smoothed, ct);

        var tagged = matched with
        {
            State = _stateMachine.CurrentState,
            UncertaintyRadiusMeters = _stateMachine.UncertaintyRadiusMeters
        };

        _lastDeadReckoned = tagged;

        if (_stateMachine.CurrentState != PositionState.DeadReckoning)
            return null;

        Volatile.Write(ref _published, tagged);

        RaiseEvaluated(sample, accepted: true, RejectionReason.None);
        _positionUpdated?.Invoke(this, tagged);

        return tagged;
    }

    private void OnLocationBecameUnavailable(object? sender, EventArgs e)
    {
        Volatile.Write(ref _published, null);
        _locationUnavailable?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseEvaluated(RawPositionSample sample, bool accepted, RejectionReason reason, string? diagnostics = null) =>
        _positionEvaluated?.Invoke(this, new PositionEvaluatedEventArgs(sample, accepted, reason, diagnostics));
}
