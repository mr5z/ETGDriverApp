namespace ETGDriverApp.Core.Models;

public enum PositionSourceType
{
    Continuous,
    Forced,
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

public enum RejectionReason
{
    None,
    AccuracyBelowThreshold,
    ImplausibleJump,
    OutOfOrderTimestamp
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
}

public record NormalizedPosition(
    double Latitude,
    double Longitude,
    double AccuracyMeters,
    PositionSourceType SourceType,
    DateTimeOffset Timestamp,
    PositionState State,
    double? UncertaintyRadiusMeters = null)
{
    // the radius consumers should draw: reported accuracy for a real fix,
    // the state machine's grown uncertainty while extrapolating
    public double EffectiveRadiusMeters =>
        Math.Max(
            UncertaintyRadiusMeters ?? 0,
            Accuracy.IsKnown(AccuracyMeters) ? AccuracyMeters : 0);
}
