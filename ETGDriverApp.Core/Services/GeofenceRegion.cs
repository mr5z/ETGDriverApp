using ETGDriverApp.Core.Models;

namespace ETGDriverApp.Core.Services;

public enum GeofenceTransition { Entered, Exited }

public enum GeofenceEventConfidence { Suppressed, LowConfidence, Trusted }

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
