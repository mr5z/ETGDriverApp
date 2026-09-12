using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Models;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Services.Filters;

internal class AccuracyGate(IOptionsMonitor<PositioningOptions> options) : IAccuracyGate
{
    AccuracyTier IAccuracyGate.Classify(RawPositionSample sample)
    {
        // a DR estimate's plausibility is the filter's business; the gate
        // exists to screen real fixes
        if (sample.IsDeadReckoned)
            return AccuracyTier.Good;

        if (!Accuracy.IsKnown(sample.AccuracyMeters))
            return AccuracyTier.Rejected;

        // one snapshot for the whole classification: a reload between the
        // two comparisons could otherwise put a sample in neither band
        var gate = options.CurrentValue.AccuracyGate;

        if (sample.AccuracyMeters > gate.RejectAboveMeters)
            return AccuracyTier.Rejected;

        if (sample.AccuracyMeters > gate.RejectAboveMeters - gate.DegradedBandMeters)
            return AccuracyTier.Borderline;

        return AccuracyTier.Good;
    }
}

internal class NoOpMapMatcher : IMapMatcher
{
    Task<NormalizedPosition> IMapMatcher.SnapToRoadAsync(
        NormalizedPosition smoothed, CancellationToken ct) =>
        Task.FromResult(smoothed);
}
