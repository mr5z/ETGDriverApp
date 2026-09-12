using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Models;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Services;

public interface IGeofenceRegistry
{
    // replaces any region with the same Id
    void Add(IGeofenceRegion region);

    bool Remove(string regionId);

    int RemoveWhere(Func<IGeofenceRegion, bool> predicate);

    IReadOnlyList<string> ActiveRegionIds { get; }
}

public interface IGeofenceEvaluator
{
    void OnPositionUpdated(NormalizedPosition position);
}

internal class GeofenceEvaluator(IOptionsMonitor<PositioningOptions> options)
    : IGeofenceEvaluator, IGeofenceRegistry
{
    private readonly Dictionary<string, TrackedRegion> _tracked = [];
    private readonly Lock _sync = new();

    private sealed class TrackedRegion(IGeofenceRegion region)
    {
        public IGeofenceRegion Region { get; } = region;

        // null until the first position after arming establishes the
        // baseline, so arming a fence the driver is already inside fires
        // nothing
        public bool? Inside { get; set; }

        public DateTimeOffset? InsideSince { get; set; }

        // a crossing observed during DR, held until a trusted fix confirms
        // or contradicts it
        public GeofenceTransition? PendingFromDeadReckoning { get; set; }
    }

    private readonly record struct PendingEvent(
        IGeofenceRegion Region,
        GeofenceTransition Transition,
        GeofenceEventConfidence Confidence);

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

        List<PendingEvent> toRaise = [];

        lock (_sync)
        {
            foreach (var tracked in _tracked.Values)
                EvaluateRegion(tracked, position, geofence, toRaise);
        }

        // dispatched outside the lock so a handler may arm or disarm fences
        foreach (var (region, transition, confidence) in toRaise)
            region.RaiseEvent(transition, confidence);
    }

    private static void EvaluateRegion(
        TrackedRegion tracked,
        NormalizedPosition position,
        GeofenceOptions geofence,
        List<PendingEvent> toRaise)
    {
        var region = tracked.Region;

        if (tracked.Inside is not { } wasInside)
        {
            var initiallyInside = region.Contains(position.Latitude, position.Longitude);

            tracked.Inside = initiallyInside;
            tracked.InsideSince = initiallyInside ? position.Timestamp : null;

            return;
        }

        // once inside, leaving is judged against the exit boundary
        var inside = wasInside
            ? region.ContainsForExit(position.Latitude, position.Longitude)
            : region.Contains(position.Latitude, position.Longitude);

        var confidence = ConfidenceFor(position);

        if (inside == wasInside)
        {
            if (!inside)
                tracked.InsideSince = null;

            ConfirmPendingIfConsistent(tracked, inside, confidence, toRaise);

            return;
        }

        // a crossing inside our own error radius is indistinguishable from drift
        var distanceToBoundary = wasInside
            ? region.DistanceToExitBoundaryMeters(position.Latitude, position.Longitude)
            : region.DistanceToBoundaryMeters(position.Latitude, position.Longitude);

        if (distanceToBoundary < position.EffectiveRadiusMeters)
            return;

        // dwell is measured against fix timestamps, not wall clock
        if (inside && region.EnterDwell > TimeSpan.Zero)
        {
            if (tracked.InsideSince is not { } since)
            {
                tracked.InsideSince = position.Timestamp;

                return;
            }

            // the dwell exists to rule out a drive-by. A trusted fix well inside the
            // region already rules it out: a vehicle passing through is never this
            // far in with this little uncertainty.
            var wellInside =
                confidence == GeofenceEventConfidence.Trusted &&
                region.DistanceToBoundaryMeters(position.Latitude, position.Longitude)
                > position.EffectiveRadiusMeters * geofence.WellInsideRadiusMultiplier;

            if (!wellInside && position.Timestamp - since < region.EnterDwell)
                return;
        }

        if (!inside)
            tracked.InsideSince = null;

        var transition = inside ? GeofenceTransition.Entered : GeofenceTransition.Exited;

        tracked.Inside = inside;

        if (confidence == GeofenceEventConfidence.Suppressed)
        {
            tracked.PendingFromDeadReckoning = transition;

            return;
        }

        tracked.PendingFromDeadReckoning = null;

        toRaise.Add(new PendingEvent(region, transition, confidence));
    }

    // a crossing observed during DR is held, then confirmed or rolled back
    // against the first trusted fix
    private static void ConfirmPendingIfConsistent(
        TrackedRegion tracked,
        bool inside,
        GeofenceEventConfidence confidence,
        List<PendingEvent> toRaise)
    {
        if (confidence is GeofenceEventConfidence.Suppressed)
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

    private static GeofenceEventConfidence ConfidenceFor(NormalizedPosition position) =>
        position.State switch
        {
            PositionState.DeadReckoning => GeofenceEventConfidence.Suppressed,
            PositionState.Degraded or PositionState.Reacquiring => GeofenceEventConfidence.LowConfidence,
            _ => GeofenceEventConfidence.Trusted
        };
}
