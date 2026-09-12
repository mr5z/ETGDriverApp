namespace ETGDriverApp.Core.Services.Jobs;

// The driver's assertion that they are at a site, stamped with what
// positioning believed at the moment they made it.
//
// This is not a status. It records one fact - a human said "I am here" - and
// the quality of the machine evidence standing behind it. What that means
// for a job is the consumer's business; Core has no opinion and no enum of
// workflow states.
//
// BasisConfidence is null when there was no arrival behind the assertion at
// all: the driver confirmed while the fence had seen nothing.
public record ArrivalConfirmation(
    string RegionId,
    GeofenceEventConfidence? BasisConfidence,
    DateTimeOffset At);

// How a later observation bears on a confirmation already made.
public enum ConfirmationStanding
{
    // nothing better than what the confirmation already rested on
    Unchanged,

    // better evidence, and it agrees
    Strengthened,

    // better evidence, and it puts the driver outside by more than the
    // uncertainty can explain
    Contradicted
}

public static class ArrivalStanding
{
    // Pure. No state, no events, no held pending crossings - the whole
    // retraction problem reduces to comparing one observation against the
    // evidence a confirmation was made on.
    //
    // The consumer decides what Contradicted costs it. Core only says that
    // the machine evidence has moved against the human's claim.
    public static ConfirmationStanding Evaluate(
        ArrivalConfirmation confirmation, RegionObservation observation)
    {
        if (observation.RegionId != confirmation.RegionId)
            return ConfirmationStanding.Unchanged;

        // a confirmation is only ever re-judged on evidence better than the
        // evidence it was made on. Equal or worse tells us nothing new.
        if (confirmation.BasisConfidence is { } basis && observation.Confidence <= basis)
            return ConfirmationStanding.Unchanged;

        if (observation.Confidence == GeofenceEventConfidence.Suppressed)
            return ConfirmationStanding.Unchanged;

        if (observation.Inside)
            return ConfirmationStanding.Strengthened;

        // outside, but not by more than we could be wrong by. Saying nothing
        // is correct here: this is the case the old drift gate existed for.
        return observation.DistanceToBoundaryMeters > observation.EffectiveRadiusMeters
            ? ConfirmationStanding.Contradicted
            : ConfirmationStanding.Unchanged;
    }
}
