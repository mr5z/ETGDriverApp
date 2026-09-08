using ETGDriverApp.Core.Models;
using ETGDriverApp.Core.Services;

namespace ETGDriverApp.Core.Services.Jobs;

internal interface IJobSiteMonitor
{
    void ArmForJob(JobAssignment job);

    void DisarmJob(string jobId);

    // the driver's confirm action; valid in either OnSiteMode
    Task ConfirmOnSiteAsync(string jobId, CancellationToken ct = default);

    // the driver is at the pickup but the app will not set the status itself
    event EventHandler<OnSiteAvailableEventArgs> OnSiteAvailable;

    event EventHandler<OnSiteEvent> OnSiteSet;
}

internal record OnSiteAvailableEventArgs(
    string JobId,
    OnSiteMode Mode,
    GeofenceEventConfidence Confidence);

internal class JobSiteMonitor(
    IGeofenceRegistry registry,
    IJobStatusWriter statusWriter,
    TimeProvider clock) : IJobSiteMonitor
{
    private static readonly TimeSpan PickupDwell = TimeSpan.FromSeconds(30);

    private readonly Lock _sync = new();
    private readonly Dictionary<string, JobAssignment> _armed = [];
    private readonly HashSet<string> _onSiteAlreadySet = [];

    private EventHandler<OnSiteAvailableEventArgs>? _onSiteAvailable;
    private EventHandler<OnSiteEvent>? _onSiteSet;

    event EventHandler<OnSiteAvailableEventArgs> IJobSiteMonitor.OnSiteAvailable
    {
        add => _onSiteAvailable += value;
        remove => _onSiteAvailable -= value;
    }

    event EventHandler<OnSiteEvent> IJobSiteMonitor.OnSiteSet
    {
        add => _onSiteSet += value;
        remove => _onSiteSet -= value;
    }

    void IJobSiteMonitor.ArmForJob(JobAssignment job)
    {
        lock (_sync)
        {
            _armed[job.JobId] = job;
            _onSiteAlreadySet.Remove(job.JobId);
        }

        registry.Add(new CircularGeofenceRegion(
            id: PickupRegionId(job.JobId),
            centerLatitude: job.Pickup.Latitude,
            centerLongitude: job.Pickup.Longitude,
            radiusMeters: job.Pickup.GeofenceRadiusMeters,
            onEvent: (_, transition, confidence) =>
                OnPickupTransition(job.JobId, transition, confidence),
            enterDwell: PickupDwell));
    }

    void IJobSiteMonitor.DisarmJob(string jobId)
    {
        lock (_sync)
        {
            _armed.Remove(jobId);
            _onSiteAlreadySet.Remove(jobId);
        }

        registry.RemoveWhere(r => r.Id.StartsWith($"job:{jobId}:", StringComparison.Ordinal));
    }

    async Task IJobSiteMonitor.ConfirmOnSiteAsync(string jobId, CancellationToken ct)
    {
        lock (_sync)
        {
            if (!_armed.ContainsKey(jobId))
                return;

            // latch, so a double tap or a tap after auto-fire is a no-op
            if (!_onSiteAlreadySet.Add(jobId))
                return;
        }

        await SetOnSiteAsync(
            new OnSiteEvent(jobId, OnSiteTrigger.DriverConfirmed,
                GeofenceEventConfidence.Trusted, clock.GetUtcNow()),
            ct);
    }

    private void OnPickupTransition(
        string jobId, GeofenceTransition transition, GeofenceEventConfidence confidence)
    {
        if (transition != GeofenceTransition.Entered)
            return;

        JobAssignment? job;

        lock (_sync)
        {
            if (!_armed.TryGetValue(jobId, out job))
                return;

            if (_onSiteAlreadySet.Contains(jobId))
                return;
        }

        // an Automatic site seen only through a LowConfidence fix falls back
        // to the manual path rather than writing a timestamp it cannot defend
        var automatic =
            job.Pickup.OnSiteMode == OnSiteMode.Automatic &&
            confidence == GeofenceEventConfidence.Trusted;

        if (!automatic)
        {
            _onSiteAvailable?.Invoke(this,
                new OnSiteAvailableEventArgs(jobId, job.Pickup.OnSiteMode, confidence));

            return;
        }

        lock (_sync)
        {
            if (!_onSiteAlreadySet.Add(jobId))
                return;
        }

        _ = SetOnSiteAsync(
            new OnSiteEvent(jobId, OnSiteTrigger.AutomaticGeofence, confidence, clock.GetUtcNow()),
            CancellationToken.None);
    }

    private async Task SetOnSiteAsync(OnSiteEvent evidence, CancellationToken ct)
    {
        try
        {
            await statusWriter.SetStatusAsync(evidence.JobId, JobStatus.OnSite, evidence, ct);
        }
        catch
        {
            // un-latch so the driver can retry rather than being stuck
            // showing ON_SITE locally
            lock (_sync)
                _onSiteAlreadySet.Remove(evidence.JobId);

            throw;
        }

        _onSiteSet?.Invoke(this, evidence);

        registry.Remove(PickupRegionId(evidence.JobId));

        JobAssignment? job;

        lock (_sync)
            _armed.TryGetValue(evidence.JobId, out job);

        if (job?.Dropoff is { } dropoff)
        {
            registry.Add(new CircularGeofenceRegion(
                id: DropoffRegionId(evidence.JobId),
                centerLatitude: dropoff.Latitude,
                centerLongitude: dropoff.Longitude,
                radiusMeters: dropoff.GeofenceRadiusMeters,
                onEvent: (_, _, _) => { },
                enterDwell: PickupDwell));
        }
    }

    private static string PickupRegionId(string jobId) => $"job:{jobId}:pickup";

    private static string DropoffRegionId(string jobId) => $"job:{jobId}:dropoff";
}
