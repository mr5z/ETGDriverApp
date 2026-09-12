using ETGDriverApp.Core.Models;

namespace ETGDriverApp.Core.Services;

// Only movements. There is deliberately no "retracted" member: a crossing
// that turns out to be wrong is not a third kind of movement, it is a later
// observation disagreeing with an earlier one, and only the consumer knows
// what it did on the earlier one.
public enum GeofenceTransition { Entered, Exited }

public enum GeofenceEventConfidence { Suppressed, LowConfidence, Trusted }

// What the evaluator saw for one region on one position, before any
// transition logic is applied. Emitted on every position for every tracked
// region, so a consumer holding a belief of its own can test that belief
// against fresh evidence without re-implementing the geometry.
public record RegionObservation(
    string RegionId,
    bool Inside,
    double DistanceToBoundaryMeters,
    double EffectiveRadiusMeters,
    GeofenceEventConfidence Confidence,
    DateTimeOffset At);

public interface IGeofenceRegion
{
    string Id { get; }

    bool Contains(double latitude, double longitude);

    // used for uncertainty hysteresis
    double DistanceToBoundaryMeters(double latitude, double longitude);

    // once inside, leaving is judged against this boundary instead, so
    // jitter at the edge can't flap the region in and out
    bool ContainsForExit(double latitude, double longitude) =>
        Contains(latitude, longitude);

    double DistanceToExitBoundaryMeters(double latitude, double longitude) =>
        DistanceToBoundaryMeters(latitude, longitude);

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

    TimeSpan IGeofenceRegion.EnterDwell => enterDwell ?? TimeSpan.Zero;

    bool IGeofenceRegion.Contains(double latitude, double longitude) =>
        DistanceFromCenter(latitude, longitude) <= radiusMeters;

    double IGeofenceRegion.DistanceToBoundaryMeters(double latitude, double longitude) =>
        Math.Abs(DistanceFromCenter(latitude, longitude) - radiusMeters);

    bool IGeofenceRegion.ContainsForExit(double latitude, double longitude) =>
        DistanceFromCenter(latitude, longitude) <= _exitRadius;

    double IGeofenceRegion.DistanceToExitBoundaryMeters(double latitude, double longitude) =>
        Math.Abs(DistanceFromCenter(latitude, longitude) - _exitRadius);

    void IGeofenceRegion.RaiseEvent(GeofenceTransition transition, GeofenceEventConfidence confidence) =>
        onEvent(id, transition, confidence);

    private double DistanceFromCenter(double latitude, double longitude) =>
        Geo.DistanceMeters(centerLatitude, centerLongitude, latitude, longitude);
}
