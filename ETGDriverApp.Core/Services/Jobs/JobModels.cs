namespace ETGDriverApp.Core.Services.Jobs;

// a place and the fence around it; whether it is the pickup or the dropoff
// comes from its role in JobAssignment
public record JobSite(
    double Latitude,
    double Longitude,
    double GeofenceRadiusMeters,
    TimeSpan? EnterDwell = null);

// Dropoff is optional: hourly and as-directed jobs have no fixed destination
public record JobAssignment(
    string JobId,
    JobSite Pickup,
    JobSite? Dropoff = null);
