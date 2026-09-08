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
    TimeSpan? enterDwell = null) : IGeofenceRegion
{
    string IGeofenceRegion.Id => id;

    TimeSpan IGeofenceRegion.EnterDwell => enterDwell ?? TimeSpan.Zero;

    bool IGeofenceRegion.Contains(double latitude, double longitude) =>
        Geo.DistanceMeters(centerLatitude, centerLongitude, latitude, longitude) <= radiusMeters;

    double IGeofenceRegion.DistanceToBoundaryMeters(double latitude, double longitude) =>
        Math.Abs(Geo.DistanceMeters(centerLatitude, centerLongitude, latitude, longitude) - radiusMeters);

    void IGeofenceRegion.RaiseEvent(GeofenceTransition transition, GeofenceEventConfidence confidence) =>
        onEvent(id, transition, confidence);
}
