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

internal class SpeedSanityChecker(double maxPlausibleSpeedKph = 300) : ISpeedSanityChecker
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxTrustedAnchorAge = TimeSpan.FromSeconds(30);

    double ISpeedSanityChecker.MaxPlausibleSpeedKph => maxPlausibleSpeedKph;

    bool ISpeedSanityChecker.Accepts(RawPositionSample candidate, NormalizedPosition lastAccepted)
    {
        var dt = candidate.Timestamp - lastAccepted.Timestamp;

        // too close together for the implied speed to mean anything
        if (dt < MinimumInterval)
            return true;
        
        // an anchor this old can't disprove anything; refusing forever is worse
        // than accepting one bad fix
        if (dt > MaxTrustedAnchorAge)
            return true;

        var distance = Geo.DistanceMeters(
            lastAccepted.Latitude, lastAccepted.Longitude,
            candidate.Latitude, candidate.Longitude);

        // both endpoints have error, so the apparent jump can exceed the real
        // one by roughly the sum of the two radii
        var errorAllowance = lastAccepted.EffectiveRadiusMeters +
                             (double.IsFinite(candidate.AccuracyMeters) ? candidate.AccuracyMeters : 0);

        var correctedDistance = Math.Max(0, distance - errorAllowance);
        var impliedKph = correctedDistance / dt.TotalSeconds * 3.6;

        return impliedKph <= maxPlausibleSpeedKph;
    }
    
    double? ISpeedSanityChecker.ImpliedKph(RawPositionSample candidate, NormalizedPosition lastAccepted)
    {
        var dt = candidate.Timestamp - lastAccepted.Timestamp;

        // too close together for the implied speed to mean anything
        if (dt < MinimumInterval)
            return null;
        
        if (dt > MaxTrustedAnchorAge)
            return null;

        var distance = Geo.DistanceMeters(
            lastAccepted.Latitude, lastAccepted.Longitude,
            candidate.Latitude, candidate.Longitude);

        // both endpoints have error, so the apparent jump can exceed the real
        // one by roughly the sum of the two radii
        var errorAllowance = lastAccepted.EffectiveRadiusMeters +
                             (double.IsFinite(candidate.AccuracyMeters) ? candidate.AccuracyMeters : 0);

        var correctedDistance = Math.Max(0, distance - errorAllowance);

        return correctedDistance / dt.TotalSeconds * 3.6;
    }
}

internal class InverseVarianceSmoother : IPositionSmoother
{
    private static readonly TimeSpan MaxUsefulGap = TimeSpan.FromSeconds(15);

    NormalizedPosition IPositionSmoother.Smooth(RawPositionSample accepted, NormalizedPosition? previous)
    {
        // a DR estimate is already filtered; averaging it against the last
        // real fix would drag it back toward where signal was lost
        if (accepted.SourceType == PositionSourceType.DeadReckoned || previous is null)
            return ToPosition(accepted, accepted.AccuracyMeters);

        var gap = accepted.Timestamp - previous.Timestamp;

        if (gap > MaxUsefulGap || gap < TimeSpan.Zero)
            return ToPosition(accepted, accepted.AccuracyMeters);

        var previousVariance = Math.Max(previous.EffectiveRadiusMeters, 1);
        previousVariance *= previousVariance;

        var sampleVariance = Math.Max(accepted.AccuracyMeters, 1);
        sampleVariance *= sampleVariance;

        var w = previousVariance / (previousVariance + sampleVariance);

        var lat = previous.Latitude + w * (accepted.Latitude - previous.Latitude);
        var lon = previous.Longitude + w * (accepted.Longitude - previous.Longitude);

        var fusedAccuracy = Math.Sqrt(previousVariance * sampleVariance / (previousVariance + sampleVariance));

        return new NormalizedPosition(
            lat, lon, fusedAccuracy, accepted.SourceType, accepted.Timestamp, previous.State);
    }

    private static NormalizedPosition ToPosition(RawPositionSample sample, double accuracy) =>
        new(sample.Latitude, sample.Longitude, accuracy, sample.SourceType,
            sample.Timestamp, PositionState.Tracking);
}

internal class NoOpMapMatcher : IMapMatcher
{
    Task<NormalizedPosition> IMapMatcher.SnapToRoadAsync(NormalizedPosition smoothed, CancellationToken ct) =>
        Task.FromResult(smoothed);
}

internal class UncertaintyWeightedBlender : IPositionBlender
{
    NormalizedPosition IPositionBlender.Blend(
        NormalizedPosition gpsDerived, NormalizedPosition deadReckoned, double gpsWeight)
    {
        var w = Math.Clamp(gpsWeight, 0, 1);

        var lat = deadReckoned.Latitude + w * (gpsDerived.Latitude - deadReckoned.Latitude);
        var lon = deadReckoned.Longitude + w * (gpsDerived.Longitude - deadReckoned.Longitude);

        var radius = w * gpsDerived.EffectiveRadiusMeters +
                     (1 - w) * deadReckoned.EffectiveRadiusMeters;

        return gpsDerived with
        {
            Latitude = lat,
            Longitude = lon,
            AccuracyMeters = radius,
            UncertaintyRadiusMeters = radius
        };
    }
}
