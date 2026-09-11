namespace ETGDriverApp.Core.Services.Jobs;

public interface IJobSiteMonitor
{
    void ArmForJob(JobAssignment job);

    void DisarmJob(string jobId);

    // the driver's confirm action; the only way ON_SITE is set
    Task ConfirmOnSiteAsync(string jobId, CancellationToken ct = default);

    // the driver has been at the pickup long enough to confirm ON_SITE
    event EventHandler<OnSiteAvailableEventArgs> OnSiteAvailable;

    // the vehicle left the pickup after ON_SITE; the consumer decides what
    // that means (en route to dropoff, no-show, ignore)
    event EventHandler<OnSiteLeftEventArgs> OnSiteLeft;
}

public record OnSiteAvailableEventArgs(
    string JobId,
    GeofenceEventConfidence Confidence);

public record OnSiteLeftEventArgs(
    string JobId,
    GeofenceEventConfidence Confidence,
    DateTimeOffset At);

internal class JobSiteMonitor(
    IGeofenceRegistry registry,
    IJobStatusWriter statusWriter,
    TimeProvider clock) : IJobSiteMonitor
{
    private static readonly TimeSpan SiteDwell = TimeSpan.FromSeconds(30);

    // leaving is judged against a wider circle so edge jitter can't fake a
    // departure
    private const double PickupExitRadiusFactor = 1.5;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, JobAssignment> _armed = [];

    // confidence of the pickup entry the driver is currently inside, kept as
    // evidence for the confirm
    private readonly Dictionary<string, GeofenceEventConfidence> _pickupEntryConfidence = [];
    private readonly HashSet<string> _onSite = [];
    private readonly HashSet<string> _left = [];


    private EventHandler<OnSiteAvailableEventArgs>? _onSiteAvailable;
    event EventHandler<OnSiteAvailableEventArgs> IJobSiteMonitor.OnSiteAvailable
    {
        add => _onSiteAvailable += value;
        remove => _onSiteAvailable -= value;
    }

    private EventHandler<OnSiteLeftEventArgs>? _onSiteLeft;
    event EventHandler<OnSiteLeftEventArgs> IJobSiteMonitor.OnSiteLeft
    {
        add => _onSiteLeft += value;
        remove => _onSiteLeft -= value;
    }

    void IJobSiteMonitor.ArmForJob(JobAssignment job)
    {
        lock (_sync)
        {
            _armed[job.JobId] = job;
            ClearJobState(job.JobId);
        }

        RegisterSite(
            PickupRegionId(job.JobId),
            job.Pickup,
            (transition, confidence) => OnPickupTransition(job.JobId, transition, confidence),
            PickupExitRadiusFactor);
    }

    void IJobSiteMonitor.DisarmJob(string jobId)
    {
        lock (_sync)
        {
            _armed.Remove(jobId);
            ClearJobState(jobId);
        }

        registry.RemoveWhere(r => r.Id.StartsWith($"job:{jobId}:", StringComparison.Ordinal));
    }

    async Task IJobSiteMonitor.ConfirmOnSiteAsync(string jobId, CancellationToken ct)
    {
        GeofenceEventConfidence? entryConfidence;

        lock (_sync)
        {
            if (!_armed.ContainsKey(jobId))
                return;

            // latch, so a double tap is a no-op
            if (!_onSite.Add(jobId))
                return;

            entryConfidence = _pickupEntryConfidence.TryGetValue(jobId, out var confidence)
                ? confidence
                : null;
        }

        var evidence = new OnSiteEvidence(jobId, entryConfidence, clock.GetUtcNow());

        try
        {
            await statusWriter.SetStatusAsync(jobId, JobStatus.OnSite, evidence, ct);
        }
        catch
        {
            // un-latch so the driver can retry rather than being stuck
            // showing ON_SITE locally
            lock (_sync)
                _onSite.Remove(jobId);

            throw;
        }
    }

    private void OnPickupTransition(
        string jobId, GeofenceTransition transition, GeofenceEventConfidence confidence)
    {
        switch (transition)
        {
            case GeofenceTransition.Entered:
                OnPickupEntered(jobId, confidence);
                break;

            case GeofenceTransition.Exited:
                OnPickupExited(jobId, confidence);
                break;
        }
    }

    private void OnPickupEntered(string jobId, GeofenceEventConfidence confidence)
    {
        lock (_sync)
        {
            if (!_armed.ContainsKey(jobId))
                return;

            if (_onSite.Contains(jobId))
                return;

            _pickupEntryConfidence[jobId] = confidence;
        }

        _onSiteAvailable?.Invoke(this, new OnSiteAvailableEventArgs(jobId, confidence));
    }

    private void OnPickupExited(string jobId, GeofenceEventConfidence confidence)
    {
        JobAssignment? job;

        lock (_sync)
        {
            if (!_armed.TryGetValue(jobId, out job))
                return;

            // leaving before ON_SITE is just driving past; the entry no
            // longer counts as evidence
            if (!_onSite.Contains(jobId))
            {
                _pickupEntryConfidence.Remove(jobId);

                return;
            }

            if (!_left.Add(jobId))
                return;
        }

        // the pickup has no further role once left
        registry.Remove(PickupRegionId(jobId));

        // registered only now, so a nearby dropoff can't fire while the
        // driver is still waiting at the pickup
        if (job.Dropoff is { } dropoff)
        {
            RegisterSite(
                DropoffRegionId(jobId),
                dropoff,
                (transition, c) => OnDropoffTransition(jobId, transition, c));
        }

        _onSiteLeft?.Invoke(this, new OnSiteLeftEventArgs(jobId, confidence, clock.GetUtcNow()));
    }

    // TODO: dropoff arrival; a placeholder until its behavior is defined
    private void OnDropoffTransition(
        string jobId, GeofenceTransition transition, GeofenceEventConfidence confidence)
    {
    }

    private void RegisterSite(
        string regionId,
        JobSite site,
        Action<GeofenceTransition, GeofenceEventConfidence> onTransition,
        double exitRadiusFactor = 1.0) =>
        registry.Add(new CircularGeofenceRegion(
            id: regionId,
            centerLatitude: site.Latitude,
            centerLongitude: site.Longitude,
            radiusMeters: site.GeofenceRadiusMeters,
            onEvent: (_, transition, confidence) => onTransition(transition, confidence),
            enterDwell: SiteDwell,
            exitRadiusMeters: site.GeofenceRadiusMeters * exitRadiusFactor));

    // caller holds _sync
    private void ClearJobState(string jobId)
    {
        _pickupEntryConfidence.Remove(jobId);
        _onSite.Remove(jobId);
        _left.Remove(jobId);
    }

    private static string PickupRegionId(string jobId) => $"job:{jobId}:pickup";

    private static string DropoffRegionId(string jobId) => $"job:{jobId}:dropoff";
}
