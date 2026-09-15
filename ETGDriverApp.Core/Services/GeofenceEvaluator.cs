using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Models;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Services;

public interface IGeofenceEvaluator
{
    // Emitted for every tracked region on every position, before any
    // transition for that same position. A consumer holding a belief of its
    // own can therefore drop it and still see the transition that follows.
    event EventHandler<RegionObservation> Observed;

    void OnPositionUpdated(NormalizedPosition position);
}

public interface IGeofenceRegistry
{
    IReadOnlyList<string> ActiveRegionIds { get; }

    void Add(IGeofenceRegion region);

    bool Remove(string regionId);

    int RemoveWhere(Func<IGeofenceRegion, bool> predicate);
}

internal class GeofenceEvaluator(IOptionsMonitor<PositioningOptions> options)
    : IGeofenceEvaluator, IGeofenceRegistry
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, TrackedRegion> _tracked = [];

    private class TrackedRegion(IGeofenceRegion region)
    {
        public IGeofenceRegion Region { get; } = region;

        // null until the first position establishes a baseline: arming
        // inside a region is not an entry
        public bool? Inside { get; set; }

        public DateTimeOffset? InsideSince { get; set; }

        // An exit observed during DR, held until a trusted fix confirms or
        // contradicts it. Only exits are held: an unverified arrival is
        // dispatched, because a human can be asked about it, whereas an
        // unverified departure has no manual counterpart.
        public GeofenceTransition? PendingFromDeadReckoning { get; set; }
    }

    private readonly record struct PendingEvent(
        IGeofenceRegion Region,
        GeofenceTransition Transition,
        GeofenceEventConfidence Confidence);

    private EventHandler<RegionObservation>? _observed;
    event EventHandler<RegionObservation> IGeofenceEvaluator.Observed
    {
        add => _observed += value;
        remove => _observed -= value;
    }

    IReadOnlyList<string> IGeofenceRegistry.ActiveRegionIds
    {
        get
        {
            lock (_sync)
                return [.. _tracked.Keys];
        }
    }

    void IGeofenceRegistry.Add(IGeofenceRegion region)
    {
        lock (_sync)
            _tracked[region.Id] = new TrackedRegion(region);
    }

    bool IGeofenceRegistry.Remove(string regionId)
    {
        lock (_sync)
            return _tracked.Remove(regionId);
    }

    int IGeofenceRegistry.RemoveWhere(Func<IGeofenceRegion, bool> predicate)
    {
        lock (_sync)
        {
            var doomed = _tracked.Values
                .Where(t => predicate(t.Region))
                .Select(t => t.Region.Id)
                .ToList();

            foreach (var id in doomed)
                _tracked.Remove(id);

            return doomed.Count;
        }
    }

    void IGeofenceEvaluator.OnPositionUpdated(NormalizedPosition position)
    {
        var geofence = options.CurrentValue.Geofence;

        List<RegionObservation> observations = [];
        List<PendingEvent> toRaise = [];

        lock (_sync)
        {
            foreach (var tracked in _tracked.Values)
            {
                observations.Add(Observe(tracked.Region, position));

                EvaluateRegion(tracked, position, geofence, toRaise);
            }
        }

        // dispatched outside the lock so a handler may arm or disarm fences.
        // Observations first: see the Observed contract.
        foreach (var observation in observations)
            _observed?.Invoke(this, observation);

        foreach (var (region, transition, confidence) in toRaise)
            region.RaiseEvent(transition, confidence);
    }

    private static RegionObservation Observe(IGeofenceRegion region, NormalizedPosition position) =>
        new(
            region.Id,
            region.OffsetFrom(position.Latitude, position.Longitude),
            position.EffectiveRadiusMeters,
            ConfidenceFor(position),
            position.Timestamp);

    private static void EvaluateRegion(
        TrackedRegion tracked,
        NormalizedPosition position,
        GeofenceOptions geofence,
        List<PendingEvent> toRaise)
    {
        var region = tracked.Region;

        if (tracked.Inside is not { } wasInside)
        {
            var initial = region.OffsetFrom(position.Latitude, position.Longitude);

            tracked.Inside = initial.Inside;
            tracked.InsideSince = initial.Inside ? position.Timestamp : null;

            return;
        }

        // once inside, leaving is judged against the exit boundary
        var offset = wasInside
            ? region.ExitOffsetFrom(position.Latitude, position.Longitude)
            : region.OffsetFrom(position.Latitude, position.Longitude);

        var inside = offset.Inside;
        var confidence = ConfidenceFor(position);

        if (inside == wasInside)
        {
            if (!inside)
                tracked.InsideSince = null;

            ConfirmPendingIfConsistent(tracked, inside, confidence, toRaise);

            return;
        }

        // The drift gate exists to stop an automatic event firing on noise.
        // An unverified enter triggers nothing automatic - it only tells the
        // consumer an arrival is worth offering to a human - so it is judged
        // on containment alone. Without this exemption the gate is
        // unsatisfiable during DR: the accumulated accuracy radius is
        // routinely wider than a site fence, so a crossing in a dead zone
        // would never be seen at all.
        //
        // But "judged on containment alone" is not "judged on nothing". Once
        // our claimed error runs to several times the fence, being inside it
        // says nothing worth asking a human about, and the offer is withheld.
        // That bound is the only place DR error and fence radius are ever
        // compared.
        //
        // IsDefensible is deliberately NOT consulted here, and that was a
        // considered choice rather than an oversight. An unverified enter asks
        // a human a question; it triggers nothing automatic, the confirmation
        // is stamped Unverified, and a later Trusted fix can still contradict
        // it through ArrivalStanding. Blind time is the wrong gate for that
        // question - captured runs at 300s blind produced true errors of
        // 1691m, 95m and 10m, so elapsed time separates none of those cases.
        // The error radius does: at MaxUnverifiedRadiusMultiplier = 3 the
        // first is refused and the other two are offered. State.MaxBlindSeconds
        // and State.MaxUsefulUncertaintyMeters therefore remain diagnostic
        // signals to the host, and gate no arrival.
        var offeringUnverifiedEnter =
            inside &&
            confidence == GeofenceEventConfidence.Unverified &&
            position.EffectiveRadiusMeters <= region.RadiusMeters * geofence.MaxUnverifiedRadiusMultiplier;

        // a crossing inside our own error radius is indistinguishable from drift
        if (!offeringUnverifiedEnter && offset.WithinNoiseOf(position.EffectiveRadiusMeters))
            return;

        // dwell is measured against fix timestamps, not wall clock
        if (inside && region.EnterDwell > TimeSpan.Zero)
        {
            if (tracked.InsideSince is not { } since)
            {
                // Also the reason no enter ever fires on a single fix when a
                // dwell is configured: the first inside fix only starts the
                // clock. That costs the well-inside shortcut below one fix of
                // latency, and buys protection against a lone multipath fix -
                // Trusted only means the state is Tracking, and a confident
                // fix in an urban canyon can still be tens of metres out. Do
                // not hoist the shortcut above this.
                tracked.InsideSince = position.Timestamp;

                return;
            }

            // The dwell exists to rule out a drive-by. A trusted fix well
            // inside the region already rules it out: a vehicle passing
            // through is never this far in with this little uncertainty.
            // ClearOf cannot be satisfied from outside the region, so this
            // no longer depends on `inside` having been checked separately.
            var wellInside =
                confidence == GeofenceEventConfidence.Trusted &&
                region.OffsetFrom(position.Latitude, position.Longitude)
                    .ClearOf(position.EffectiveRadiusMeters * geofence.WellInsideRadiusMultiplier);

            if (!wellInside && position.Timestamp - since < region.EnterDwell)
                return;
        }

        if (!inside)
            tracked.InsideSince = null;

        var transition = inside ? GeofenceTransition.Entered : GeofenceTransition.Exited;

        tracked.Inside = inside;

        if (confidence == GeofenceEventConfidence.Unverified && !inside)
        {
            tracked.PendingFromDeadReckoning = transition;
            return;
        }

        tracked.PendingFromDeadReckoning = null;

        toRaise.Add(new PendingEvent(region, transition, confidence));
    }

    // an exit observed during DR is held, then confirmed or rolled back
    // against the first trusted fix
    private static void ConfirmPendingIfConsistent(
        TrackedRegion tracked,
        bool inside,
        GeofenceEventConfidence confidence,
        List<PendingEvent> toRaise)
    {
        if (confidence is GeofenceEventConfidence.Unverified)
            return;

        if (tracked.PendingFromDeadReckoning is not { } pending)
            return;

        tracked.PendingFromDeadReckoning = null;

        var impliedByPending = pending == GeofenceTransition.Entered;

        if (impliedByPending == inside)
            toRaise.Add(new PendingEvent(tracked.Region, pending, GeofenceEventConfidence.LowConfidence));
        else
            tracked.Inside = inside;
    }

    // Every arm is explicit. This used to end in `_ => Trusted`, which made
    // the highest grade of evidence the fall-through - so any state added
    // later would be Trusted until someone remembered this switch.
    private static GeofenceEventConfidence ConfidenceFor(NormalizedPosition position)
    {
        // The state machine has declared this position indefensible. It used
        // to say so only by raising an event, and the only subscriber wrote
        // a log line - so MaxBlindSeconds and MaxUsefulUncertaintyMeters cut
        // nothing at all. A captured run kept offering arrivals for three and
        // a half minutes after LocationUnavailable fired.
        if (!position.IsDefensible)
            return GeofenceEventConfidence.Unverified;

        return position.State switch
        {
            // no position at all cannot be evidence of anything
            PositionState.NoFix or PositionState.DeadReckoning =>
                GeofenceEventConfidence.Unverified,
            PositionState.Degraded or PositionState.Reacquiring =>
                GeofenceEventConfidence.LowConfidence,
            PositionState.Tracking =>
                GeofenceEventConfidence.Trusted,
            _ => GeofenceEventConfidence.Unverified
        };
    }
}