using ETGDriverApp.Core.Models;

namespace ETGDriverApp.Core.Services.Filters;

internal class AccuracyGate(
    double accuracyThresholdMeters = 50,
    double degradedBandMeters = 20) : IAccuracyGate
{
    AccuracyTier IAccuracyGate.Classify(RawPositionSample sample)
    {
        // a DR estimate's plausibility is the filter's business; the gate
        // exists to screen real fixes
        if (sample.SourceType == PositionSourceType.DeadReckoned)
            return AccuracyTier.Good;

        // MAUI surfaces "no accuracy reported" as null, mapped to MaxValue
        if (!double.IsFinite(sample.AccuracyMeters) || sample.AccuracyMeters < 0)
            return AccuracyTier.Rejected;

        if (sample.AccuracyMeters > accuracyThresholdMeters)
            return AccuracyTier.Rejected;

        if (sample.AccuracyMeters > accuracyThresholdMeters - degradedBandMeters)
            return AccuracyTier.Borderline;

        return AccuracyTier.Good;
    }
}

internal class NoOpMapMatcher : IMapMatcher
{
    Task<NormalizedPosition> IMapMatcher.SnapToRoadAsync(NormalizedPosition smoothed, CancellationToken ct) =>
        Task.FromResult(smoothed);
}
