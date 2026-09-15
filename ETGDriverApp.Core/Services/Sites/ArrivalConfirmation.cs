using ETGDriverApp.Core.Services;

namespace ETGDriverApp.Core.Services.Sites;

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
//
// RegionId was renamed SiteKey. Same value, and it was always the same value
// - the old name described where it came from rather than what it is, and
// the rename is what lets the monitor key on it directly.
public record ArrivalConfirmation(
    string SiteKey,
    GeofenceEventConfidence? BasisConfidence,
    DateTimeOffset At);

// How a later observation bears on a belief already held.
public enum ConfirmationStanding
{
    // nothing better than what the belief already rested on
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
    // evidence a belief was formed on.
    //
    // Public because the caller may hold beliefs of its own and should test
    // them with this function rather than reimplementing the geometry. The
    // monitor uses it for both confirmations and unanswered offers.
    //
    // The consumer decides what Contradicted costs it. Core only says that
    // the machine evidence has moved against the claim.
    public static ConfirmationStanding Evaluate(
        ArrivalConfirmation confirmation, RegionObservation observation)
    {
        if (observation.SiteKey != confirmation.SiteKey)
            return ConfirmationStanding.Unchanged;

        // a belief is only ever re-judged on evidence better than the
        // evidence it was formed on. Equal or worse tells us nothing new.
        if (confirmation.BasisConfidence is { } basis && observation.Confidence <= basis)
            return ConfirmationStanding.Unchanged;

        if (observation.Confidence == GeofenceEventConfidence.Suppressed)
            return ConfirmationStanding.Unchanged;

        // Outside, but not by more than we could be wrong by, is not evidence
        // of anything: saying nothing is correct there. This is the case the
        // evaluator's drift gate exists for, and WithinNoiseOf is the same
        // test it uses, so the two cannot drift apart.
        if (observation.Offset.WithinNoiseOf(observation.EffectiveRadiusMeters))
            return ConfirmationStanding.Unchanged;

        if (observation.Offset.Inside)
            return ConfirmationStanding.Strengthened;

        // Retraction needs the top grade, and only retraction does.
        //
        // A reseed out of dead reckoning surfaces as Reacquiring, which is
        // LowConfidence, which beats a Suppressed basis - so one adopted fix
        // could overturn the driver. Strengthening on the same evidence is
        // free; being wrong about it costs nothing. Contradicting is not.
        return observation.Confidence == GeofenceEventConfidence.Trusted
            ? ConfirmationStanding.Contradicted
            : ConfirmationStanding.Unchanged;
    }
}
