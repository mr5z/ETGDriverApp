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

public enum AdmissionDecision
{
    Accept,

    Reject,

    // Not rejected on its own merits, but not adopted either: the sample is
    // held as a candidate and the next one decides. A lone outlier dies here;
    // a genuine relocation costs one tick.
    HoldForConfirmation
}

// How the sample is to be applied if admitted. The pipeline needs to know
// this because the two are different operations on the filter: an update
// folds the measurement into existing state, a re-seed discards that state
// and starts from the measurement.
public enum AdmissionEffect
{
    Update,
    Reseed
}

public readonly record struct AdmissionVerdict(
    AdmissionDecision Decision,
    AdmissionEffect Effect,
    RejectionReason Reason = RejectionReason.None,
    string? Diagnostics = null)
{
    public bool Accepted => Decision == AdmissionDecision.Accept;

    public static AdmissionVerdict Update() =>
        new(AdmissionDecision.Accept, AdmissionEffect.Update);

    public static AdmissionVerdict Reseed() =>
        new(AdmissionDecision.Accept, AdmissionEffect.Reseed);

    public static AdmissionVerdict Reject(RejectionReason reason, string? diagnostics = null) =>
        new(AdmissionDecision.Reject, AdmissionEffect.Update, reason, diagnostics);

    public static AdmissionVerdict Hold(RejectionReason reason, string? diagnostics = null) =>
        new(AdmissionDecision.HoldForConfirmation, AdmissionEffect.Update, reason, diagnostics);
}

// Everything the gate needs to pick a reference frame. Passed as one value so
// that adding a frame later does not change every call site.
internal readonly record struct AdmissionContext(
    RawPositionSample Sample,
    PositionKalmanFilter Filter,
    NormalizedPosition? Published,
    bool Extrapolating);

// Decides whether a sample may touch the track, and against what.
//
// This replaces an if/else in the pipeline where one branch consulted the
// plausibility gate and the other did not. That asymmetry was the bug: while
// dead reckoning, the filter holds only extrapolations, so judging an
// incoming fix against it would lock out recovery - and the code's answer was
// to skip the check entirely. Any single fix arriving during DeadReckoning
// therefore became the new truth however far away it was; a misconfigured
// mock itinerary relocated a shift 6,200km in one tick without producing a
// rejection.
//
// The fix is not another guard. It is noticing that "no trustworthy filter"
// does not mean "no reference frame available": the last published position,
// plus how far the vehicle could have travelled while blind, is a perfectly
// good frame. The gate picks between them.
internal interface IAdmissionGate
{
    AdmissionVerdict Evaluate(AdmissionContext context);
}