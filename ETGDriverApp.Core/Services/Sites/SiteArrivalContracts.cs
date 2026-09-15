using ETGDriverApp.Core.Services;

namespace ETGDriverApp.Core.Services.Sites;

// A place and the fence around it. Nothing about what the place is FOR.
//
// This is the whole of what positioning needs to watch somewhere. A pickup,
// a dropoff, a depot, a rest stop and a customer's gate are the same object
// here; the difference between them is a fact about a job, and jobs are the
// caller's business. Core never learns which is which and never orders them.
//
// The tunables are per-site rather than global because they are facts about
// the place, not policy: a busy depot needs more exit hysteresis than a
// residential address, and a yard with a long approach road needs a longer
// dwell than a kerbside stop. The caller knows these; Core does not.
public record SiteDefinition(
    double Latitude,
    double Longitude,
    double GeofenceRadiusMeters,

    // Leaving is judged against this multiple of the radius, so edge jitter
    // cannot fake a departure. 1 means no hysteresis.
    double ExitRadiusFactor = 1,

    // How long containment must persist before an arrival is offered.
    // Null takes GeofenceOptions.DefaultEnterDwell.
    TimeSpan? EnterDwell = null);

// An arrival worth offering to the driver, or one being withdrawn.
public record SiteArrivalEventArgs(
    string SiteKey,
    GeofenceEventConfidence Confidence,
    DateTimeOffset At);

// Better evidence has moved against something the driver already confirmed.
// The confirmation is dropped before this is raised.
public record ArrivalContradictedEventArgs(
    string SiteKey,
    ArrivalConfirmation Dropped,
    DateTimeOffset At);

// The vehicle left a site it had arrived at. What that means - en route,
// no-show, job complete, nothing at all - is the caller's to decide.
public record SiteLeftEventArgs(
    string SiteKey,
    GeofenceEventConfidence Confidence,
    DateTimeOffset At);

// Watches places, and tracks the evidence standing behind an arrival at one.
//
// The division of labour, stated once so it is not re-litigated at every
// call site:
//
//   Core owns    - when containment counts as an arrival, what grade of
//                  evidence stands behind it, when that evidence has been
//                  withdrawn, and the integrity of a confirmation record.
//   Caller owns  - which places are worth watching, in what order, what a
//                  key means, and what any of it does to a job.
//
// Site keys are opaque strings chosen by the caller. Core compares them and
// nothing else. "job-4711:0" and "site-a" are equally valid; Core will not
// parse either.
public interface ISiteArrivalMonitor
{
    IReadOnlyList<string> WatchedSiteKeys { get; }

    // Idempotent on the key: watching a key already watched replaces the
    // definition and clears any standing built against the old one, because
    // a moved fence invalidates the evidence gathered inside it.
    void Watch(string siteKey, SiteDefinition site);

    bool Unwatch(string siteKey);

    int UnwatchAll();

    // The driver's assertion that they are at a site, stamped with what
    // positioning believed at the moment they made it. Synchronous and
    // inbound: nothing is written anywhere, so nothing can fail, so there is
    // no rollback to get wrong.
    //
    // Returns null if the site is not watched or was already confirmed. A
    // double tap is a no-op rather than a second record with a later time.
    ArrivalConfirmation? Confirm(string siteKey);

    ArrivalConfirmation? ConfirmationFor(string siteKey);

    // An arrival worth offering. Confidence can be Unverified: seen under
    // dead reckoning and not corroborated by a real fix yet. May be raised
    // more than once for the same site as the evidence improves.
    event EventHandler<SiteArrivalEventArgs> ArrivalAvailable;

    // An offer previously made is no longer supported by the evidence.
    //
    // Distinct from ArrivalContradicted, and the distinction matters to the
    // caller: nothing was confirmed, so nothing is being taken back from the
    // driver. There is no record to reverse and nobody to notify. Withdrawing
    // the affordance silently is the correct response.
    event EventHandler<SiteArrivalEventArgs> ArrivalLapsed;

    event EventHandler<ArrivalContradictedEventArgs> ArrivalContradicted;

    event EventHandler<SiteLeftEventArgs> SiteLeft;
}
