namespace ETGDriverApp.Core.Models;

// Only the stages the pipeline actually runs live here.
//
// Removed in this clean-up, along with their commented-out DI registrations:
//   ISpeedSanityChecker - superseded by PlausibilityGate, which judges speed
//                         against the filter's velocity state rather than a
//                         difference of two positions.
//   IPositionSmoother   - superseded by PositionKalmanFilter.
//   IPositionBlender    - DR is blended by feeding it through the filter,
//                         not by a weighted average of two positions.
// None of them had an implementation registered. If you still need one,
// recover it from source control rather than leaving the interface as a
// decoy for the next reader.
public interface IAccuracyGate
{
    AccuracyTier Classify(RawPositionSample sample);
}

public interface IMapMatcher
{
    Task<NormalizedPosition> SnapToRoadAsync(
        NormalizedPosition smoothed, CancellationToken ct = default);
}
