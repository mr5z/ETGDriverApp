using ETGDriverApp.Core.Helpers;

namespace ETGDriverApp.Core.Services;

// Only movements. There is deliberately no "retracted" member: a crossing
// that turns out to be wrong is not a third kind of movement, it is a later
// observation disagreeing with an earlier one, and only the consumer knows
// what it did on the earlier one.
public enum GeofenceTransition { Entered, Exited }

public enum GeofenceEventConfidence { Unverified, LowConfidence, Trusted }

// Where a position sits relative to a boundary. Distance and containment are
// one fact, not two: returning a bare unsigned double made "80 m from the
// boundary" ambiguous between 80 m inside and 80 m outside, so every call
// site had to remember to pair it with a separate Contains() call, and a
// consumer reading the number off a RegionObservation could forget.
public readonly record struct BoundaryOffset(double Meters, bool Inside)
{
    // Positive inside, negative outside. For callers that want to do their
    // own arithmetic rather than use the predicates below.
    public double Inward => Inside ? Meters : -Meters;

    // Far enough inside to be clear of the boundary by the given margin.
    // Cannot be satisfied by a point outside the region, which is what makes
    // the evaluator's "well inside" test correct by construction rather than
    // by remembering to check containment alongside it.
    public bool ClearOf(double marginMeters) => Inside && Meters > marginMeters;

    // Close enough to the boundary that the crossing is indistinguishable
    // from our own error. Deliberately side-agnostic: drift is drift whether
    // it carried us in or out.
    public bool WithinNoiseOf(double marginMeters) => Meters < marginMeters;
}

// What the evaluator saw for one region on one position, before any
// transition logic is applied. Emitted on every position for every tracked
// region, so a consumer holding a belief of its own can test that belief
// against fresh evidence without re-implementing the geometry.
//
// SiteKey was RegionId. Same value - the registry key - but the old name
// described where it came from rather than what a consumer does with it,
// and consumers key their own state on it.
public record RegionObservation(
    string SiteKey,
    BoundaryOffset Offset,
    double EffectiveRadiusMeters,
    GeofenceEventConfidence Confidence,
    DateTimeOffset At)
{
    public bool Inside => Offset.Inside;
}

public interface IGeofenceRegion
{
    string Id { get; }

    // The enter boundary, exposed so a consumer can relate its own
    // uncertainty to the size of the thing it is deciding about. Nothing
    // inside this file uses it; it exists because "is my error radius
    // commensurate with this fence" is a question several callers need to
    // ask and none of them can ask without it.
    double RadiusMeters { get; }

    // carries containment, so there is no separate Contains()
    BoundaryOffset OffsetFrom(double latitude, double longitude);

    // once inside, leaving is judged against this boundary instead, so
    // jitter at the edge can't flap the region in and out
    BoundaryOffset ExitOffsetFrom(double latitude, double longitude) =>
        OffsetFrom(latitude, longitude);

    // how long containment must persist before an Entered event is dispatched
    TimeSpan EnterDwell => TimeSpan.Zero;

    void RaiseEvent(GeofenceTransition transition, GeofenceEventConfidence confidence);
}

internal class CircularGeofenceRegion(
    string id,
    double centerLatitude,
    double centerLongitude,
    double radiusMeters,
    Action<string, GeofenceTransition, GeofenceEventConfidence> onEvent,
    TimeSpan? enterDwell = null,
    double? exitRadiusMeters = null) : IGeofenceRegion
{
    // never smaller than the enter radius
    private readonly double _exitRadius = Math.Max(exitRadiusMeters ?? radiusMeters, radiusMeters);

    string IGeofenceRegion.Id => id;

    double IGeofenceRegion.RadiusMeters => radiusMeters;

    TimeSpan IGeofenceRegion.EnterDwell => enterDwell ?? TimeSpan.Zero;

    BoundaryOffset IGeofenceRegion.OffsetFrom(double latitude, double longitude) =>
        Offset(latitude, longitude, radiusMeters);

    BoundaryOffset IGeofenceRegion.ExitOffsetFrom(double latitude, double longitude) =>
        Offset(latitude, longitude, _exitRadius);

    void IGeofenceRegion.RaiseEvent(GeofenceTransition transition, GeofenceEventConfidence confidence) =>
        onEvent(id, transition, confidence);

    private BoundaryOffset Offset(double latitude, double longitude, double boundaryRadius)
    {
        var distance = DistanceFromCenter(latitude, longitude);

        return new BoundaryOffset(Math.Abs(distance - boundaryRadius), distance <= boundaryRadius);
    }

    private double DistanceFromCenter(double latitude, double longitude) =>
        Geo.DistanceMeters(centerLatitude, centerLongitude, latitude, longitude);
}
