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

// recorded with ON_SITE; EntryConfidence is null when the driver confirmed
// without a pickup entry behind it
public record OnSiteEvidence(
    string JobId,
    GeofenceEventConfidence? EntryConfidence,
    DateTimeOffset At);

// implemented by the head project: backend sync, offline queue, local state
public interface IJobStatusWriter
{
    Task SetStatusAsync(string jobId, JobStatus status, OnSiteEvidence? evidence = null,
        CancellationToken ct = default);
}
