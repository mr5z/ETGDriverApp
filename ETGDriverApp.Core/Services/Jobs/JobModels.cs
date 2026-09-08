namespace ETGDriverApp.Core.Services.Jobs;

public enum JobStatus
{
    Offered,
    Accepted,
    Rejected,
    EnRouteToPickup,
    OnSite,
    PassengerOnBoard,
    Completed,
    Cancelled
}

public enum OnSiteMode
{
    // geofence entry sets ON_SITE by itself
    Automatic,

    // geofence entry only unlocks the driver's confirm action
    Manual
}

// OnSiteMode should come from the job payload, decided server-side. An
// airport fence covers terminals, car parks and holding areas, so entering it
// says little about reaching the passenger.
public record JobSite(
    double Latitude,
    double Longitude,
    double GeofenceRadiusMeters,
    OnSiteMode OnSiteMode);

public record JobAssignment(
    string JobId,
    JobSite Pickup,
    JobSite? Dropoff = null);

public enum OnSiteTrigger
{
    AutomaticGeofence,
    DriverConfirmed
}

public record OnSiteEvent(
    string JobId,
    OnSiteTrigger Trigger,
    GeofenceEventConfidence Confidence,
    DateTimeOffset At);

// implemented by the head project: backend sync, offline queue, local state
internal interface IJobStatusWriter
{
    Task SetStatusAsync(string jobId, JobStatus status, OnSiteEvent? evidence = null,
        CancellationToken ct = default);
}
