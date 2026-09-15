namespace ETGDriverApp.Core.Models;

// How a sample came to exist. This is provenance, not a category of
// usefulness: every consumer that needs to know "can I treat this as an
// observation of now?" asks through the predicates below rather than
// re-deriving it from timestamps.
//
// `Cached` is the one that used to be missing. Both platforms fall back to a
// stored location when a single-shot request times out - GetLastKnownLocation
// on Android, CLLocationManager.Location on iOS - and both KNEW they had done
// so, in an `if` branch, and then threw the fact away. Downstream code was
// left to infer it from the timestamp, which is a heuristic standing in for
// something we already had. A captured session shows the consequence: the
// same two-hour-old cached fix ingested 238 times, re-anchoring dead
// reckoning onto a dead position each time and growing filter uncertainty
// past 11km.
public enum PositionSourceType
{
    // delivered by the platform's continuous subscription
    Continuous,

    // a single-shot request that actually produced a new fix
    Forced,

    // the platform's stored last-known location, returned because a
    // single-shot request timed out. Says where we were, not where we are.
    Cached,

    // our own extrapolation from the last anchor
    DeadReckoned
}

public enum PositionState
{
    NoFix,
    Tracking,
    Degraded,
    DeadReckoning,
    Reacquiring
}

// Why the position stopped being defensible. Carried on the event rather
// than inferred, because the two thresholds have different remedies: one
// says "we have been blind too long", the other says "the estimate has
// spread too far". Collapsing them into a bare notification meant whichever
// fired first silenced the other for the rest of the episode.
public enum UnusableReason
{
    None,

    // no anchor at all - nothing to extrapolate from
    NoAnchor,

    // the filter has no state to report on; the position is gone, not vague
    FixLost,

    // State.MaxUsefulUncertaintyMeters
    UncertaintyTooLarge,

    // State.MaxBlindDuration
    BlindTooLong
}

public enum RejectionReason
{
    None,
    AccuracyBelowThreshold,
    ImplausibleJump,
    OutOfOrderTimestamp,

    // the track would have to move further than the vehicle could have
    // travelled while we were blind, and no second fix has confirmed it
    UnconfirmedRelocation,

    // a cached fix cannot end a dead-reckoning episode: it is evidence about
    // the past, and adopting it would silently rewind the track
    CachedFixNotAnObservation
}

public enum AccuracyTier
{
    Rejected,
    Borderline,
    Good
}

public static class Accuracy
{
    // The platform reports "no accuracy available" as null. It used to be
    // mapped to double.MaxValue, which is a finite number: every arithmetic
    // path downstream had to remember that one specific double meant
    // "unknown", and double.IsFinite checks written to catch it did not.
    // PositiveInfinity is what "unboundedly bad" actually means, and it
    // fails IsFinite, so the existing guards catch it for free.
    public const double Unknown = double.PositiveInfinity;

    public static bool IsKnown(double accuracyMeters) =>
        double.IsFinite(accuracyMeters) && accuracyMeters >= 0;

    public static double FromReported(double? reportedMeters) =>
        reportedMeters is { } value && value >= 0 ? value : Unknown;
}

public static class Units
{
    public const double MpsToKph = 3.6;

    public const double KphToMps = 1 / MpsToKph;
}

public record RawPositionSample(
    double Latitude,
    double Longitude,
    double AccuracyMeters,
    DateTimeOffset Timestamp,
    PositionSourceType SourceType,
    double? SpeedMps = null,
    double? CourseDegrees = null)
{
    public bool IsDeadReckoned => SourceType == PositionSourceType.DeadReckoned;

    public bool IsCached => SourceType == PositionSourceType.Cached;

    // The question almost every consumer actually wants answered: did a
    // sensor observe the world at this sample's timestamp? Only these two
    // may anchor dead reckoning or end a staleness episode.
    public bool IsObserved =>
        SourceType is PositionSourceType.Continuous or PositionSourceType.Forced;
}

public record NormalizedPosition(
    double Latitude,
    double Longitude,
    double AccuracyMeters,
    PositionSourceType SourceType,
    DateTimeOffset Timestamp,
    PositionState State,
    double? UncertaintyRadiusMeters = null,
    double? EstimatedSpeedMps = null,

    // Whether the state machine still considers this position defensible.
    // Defaults true so a position built without an opinion is not silently
    // treated as worthless; the pipeline sets it from the state machine.
    bool IsDefensible = true)
{
    // The radius consumers should draw, and the one every arrival decision
    // is measured against.
    //
    // For a real fix this is the reported accuracy, widened by the filter's
    // own uncertainty if that is larger.
    //
    // For an EXTRAPOLATION it is the filter's uncertainty alone, and taking
    // the max here was wrong twice over. First, the filter has already
    // folded DR's self-reported accuracy in as the measurement variance
    // r = accuracy^2, so the max applies the same pessimism a second time.
    // Second, DR's self-report is quadratic in elapsed time and outruns
    // reality badly: a captured five-minute outage ended with DR claiming
    // 3,859 m, the filter saying 424 m, and the true error 307 m. Every
    // arrival test - the drift gate, ClearOf, WithinNoiseOf, the unverified
    // enter bound - was running on the 3,859.
    //
    // AccuracyMeters is still carried, unchanged, as provenance: it is what
    // the producer claimed, and diagnostics compare the two deliberately.
    public double EffectiveRadiusMeters =>
        SourceType == PositionSourceType.DeadReckoned
            ? UncertaintyRadiusMeters ?? FallbackRadius
            : Math.Max(UncertaintyRadiusMeters ?? 0, FallbackRadius);

    private double FallbackRadius =>
        Accuracy.IsKnown(AccuracyMeters) ? AccuracyMeters : 0;
}