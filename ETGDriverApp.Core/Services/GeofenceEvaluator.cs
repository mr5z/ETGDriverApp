using ETGDriverApp.Core.Models;

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

internal class GeofenceEvaluator : IGeofenceEvaluator, IGeofenceRegistry
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, IGeofenceRegion> _regions = [];
    private readonly Dictionary<string, bool> _lastKnownInside = [];
    private readonly Dictionary<string, DateTimeOffset> _insideSince = [];
    private readonly Dictionary<string, GeofenceTransition> _pendingFromDeadReckoning = [];

    IReadOnlyList<string> IGeofenceRegistry.ActiveRegionIds
    {
        get
        {
            lock (_sync)
                return [.. _regions.Keys];
        }
    }

    void IGeofenceRegistry.Add(IGeofenceRegion region)
    {
        lock (_sync)
        {
            _regions[region.Id] = region;

            // the first position after arming sets the baseline, so arming a
            // fence the driver is already inside fires nothing
            _lastKnownInside.Remove(region.Id);
            _insideSince.Remove(region.Id);
            _pendingFromDeadReckoning.Remove(region.Id);
        }
    }

    bool IGeofenceRegistry.Remove(string regionId)
    {
        lock (_sync)
            return RemoveCore(regionId);
    }

    int IGeofenceRegistry.RemoveWhere(Func<IGeofenceRegion, bool> predicate)
    {
        lock (_sync)
        {
            var doomed = _regions.Values.Where(predicate).Select(r => r.Id).ToList();

            foreach (var id in doomed)
                RemoveCore(id);

            return doomed.Count;
        }
    }

    void IGeofenceEvaluator.OnPositionUpdated(NormalizedPosition position)
    {
        List<(IGeofenceRegion Region, GeofenceTransition Transition, GeofenceEventConfidence Confidence)> toRaise = [];

        lock (_sync)
        {
            foreach (var region in _regions.Values)
                EvaluateRegion(region, position, toRaise);
        }

        // dispatched outside the lock so a handler may arm or disarm fences
        foreach (var (region, transition, confidence) in toRaise)
            region.RaiseEvent(transition, confidence);
    }

    private bool RemoveCore(string regionId)
    {
        _lastKnownInside.Remove(regionId);
        _insideSince.Remove(regionId);
        _pendingFromDeadReckoning.Remove(regionId);

        return _regions.Remove(regionId);
    }

    private void EvaluateRegion(
        IGeofenceRegion region,
        NormalizedPosition position,
        List<(IGeofenceRegion, GeofenceTransition, GeofenceEventConfidence)> toRaise)
    {
        if (!_lastKnownInside.TryGetValue(region.Id, out var wasInside))
        {
            var initiallyInside = region.Contains(position.Latitude, position.Longitude);

            _lastKnownInside[region.Id] = initiallyInside;

            if (initiallyInside)
                _insideSince[region.Id] = position.Timestamp;

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
                _insideSince.Remove(region.Id);

            ConfirmPendingIfConsistent(region, inside, confidence, toRaise);

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
            if (!_insideSince.TryGetValue(region.Id, out var since))
            {
                _insideSince[region.Id] = position.Timestamp;

                return;
            }

            if (position.Timestamp - since < region.EnterDwell)
                return;
        }

        if (!inside)
            _insideSince.Remove(region.Id);

        var transition = inside ? GeofenceTransition.Entered : GeofenceTransition.Exited;

        if (confidence == GeofenceEventConfidence.Suppressed)
        {
            _pendingFromDeadReckoning[region.Id] = transition;
            _lastKnownInside[region.Id] = inside;

            return;
        }

        _lastKnownInside[region.Id] = inside;
        _pendingFromDeadReckoning.Remove(region.Id);

        toRaise.Add((region, transition, confidence));
    }

    // a crossing observed during DR is held, then confirmed or rolled back
    // against the first trusted fix
    private void ConfirmPendingIfConsistent(
        IGeofenceRegion region,
        bool inside,
        GeofenceEventConfidence confidence,
        List<(IGeofenceRegion, GeofenceTransition, GeofenceEventConfidence)> toRaise)
    {
        if (confidence is GeofenceEventConfidence.Suppressed)
            return;

        if (!_pendingFromDeadReckoning.TryGetValue(region.Id, out var pending))
            return;

        _pendingFromDeadReckoning.Remove(region.Id);

        var impliedByPending = pending == GeofenceTransition.Entered;

        if (impliedByPending == inside)
            toRaise.Add((region, pending, GeofenceEventConfidence.LowConfidence));
        else
            _lastKnownInside[region.Id] = inside;
    }

    private static GeofenceEventConfidence ConfidenceFor(NormalizedPosition position) =>
        position.State switch
        {
            PositionState.DeadReckoning => GeofenceEventConfidence.Suppressed,
            PositionState.Degraded => GeofenceEventConfidence.LowConfidence,
            PositionState.Reacquiring => GeofenceEventConfidence.LowConfidence,
            _ => GeofenceEventConfidence.Trusted
        };
}
