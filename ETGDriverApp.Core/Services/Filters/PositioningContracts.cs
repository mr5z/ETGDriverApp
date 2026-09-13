using ETGDriverApp.Core.Models;

namespace ETGDriverApp.Core.Services.Filters;

public interface IAccuracyGate
{
    AccuracyTier Classify(RawPositionSample sample);
}

public interface IMapMatcher
{
    Task<NormalizedPosition> SnapToRoadAsync(
        NormalizedPosition smoothed, CancellationToken ct = default);
}
