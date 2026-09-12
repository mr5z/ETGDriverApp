namespace ETGDriverApp.Core.Services.Jobs;

public interface IJobSiteMonitor
{
    void ArmForJob(JobAssignment job);

    void DisarmJob(string jobId);

    // The driver's confirm action. Synchronous and inbound: nothing is
    // written anywhere, so nothing can fail, so there is no rollback to get
    // wrong. Returns the stamped assertion, or null if the job is not armed
    // or was already confirmed.
    ArrivalConfirmation? ConfirmArrival(string jobId);

    ArrivalConfirmation? ConfirmationFor(string jobId);

    // an arrival worth offering to the driver. Confidence can be Suppressed:
    // seen under dead reckoning and not corroborated by a real fix yet
    event EventHandler<ArrivalAvailableEventArgs> ArrivalAvailable;

    // better evidence has moved against something the driver already
    // confirmed. The confirmation is dropped before this is raised
    event EventHandler<ArrivalContradictedEventArgs> ArrivalContradicted;

    // the vehicle left the pickup after a confirmed arrival; the consumer
    // decides what that means (en route to dropoff, no-show, ignore)
    event EventHandler<SiteLeftEventArgs> SiteLeft;
}

public record ArrivalAvailableEventArgs(
    string JobId,
    GeofenceEventConfidence Confidence);

public record ArrivalContradictedEventArgs(
    string JobId,
    ArrivalConfirmation Dropped,
    DateTimeOffset At);

public record SiteLeftEventArgs(
    string JobId,
    GeofenceEventConfidence Confidence,
    DateTimeOffset At);

internal class JobSiteMonitor : IJobSiteMonitor
{
    private static readonly TimeSpan SiteDwell = TimeSpan.FromSeconds(30);

    // leaving is judged against a wider circle so edge jitter can't fake a
    // departure
    private const double PickupExitRadiusFactor = 1.5;

    private readonly IGeofenceRegistry _registry;
    private readonly TimeProvider _clock;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, JobAssignment> _armed = [];

    // confidence of the pickup entry the driver is currently inside, kept as
    // the evidence a confirmation would rest on
    private readonly Dictionary<string, GeofenceEventConfidence> _pickupEntryConfidence = [];
    private readonly Dictionary<string, ArrivalConfirmation> _confirmed = [];
    private readonly HashSet<string> _left = [];

    public JobSiteMonitor(
        IGeofenceRegistry registry,
        IGeofenceEvaluator evaluator,
        TimeProvider clock)
    {
        _registry = registry;
        _clock = clock;

        // Observations arrive before transitions for the same position, so a
        // confirmation contradicted by this fix is already gone by the time
        // the resulting Exited lands - which is what stops a bogus arrival
        // from looking like a departure.
        evaluator.Observed += OnObserved;
    }

    private EventHandler<ArrivalAvailableEventArgs>? _arrivalAvailable;
    event EventHandler<ArrivalAvailableEventArgs> IJobSiteMonitor.ArrivalAvailable
    {
        add => _arrivalAvailable += value;
        remove => _arrivalAvailable -= value;
    }

    private EventHandler<ArrivalContradictedEventArgs>? _arrivalContradicted;
    event EventHandler<ArrivalContradictedEventArgs> IJobSiteMonitor.ArrivalContradicted
    {
        add => _arrivalContradicted += value;
        remove => _arrivalContradicted -= value;
    }

    private EventHandler<SiteLeftEventArgs>? _siteLeft;
    event EventHandler<SiteLeftEventArgs> IJobSiteMonitor.SiteLeft
    {
        add => _siteLeft += value;
        remove => _siteLeft -= value;
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

        _registry.RemoveWhere(r => r.Id.StartsWith($"job:{jobId}:", StringComparison.Ordinal));
    }

    ArrivalConfirmation? IJobSiteMonitor.ConfirmArrival(string jobId)
    {
        lock (_sync)
        {
            if (!_armed.ContainsKey(jobId))
                return null;

            // a double tap is a no-op, and returns nothing rather than a
            // second confirmation with a later timestamp
            if (_confirmed.ContainsKey(jobId))
                return null;

            var basis = _pickupEntryConfidence.TryGetValue(jobId, out var confidence)
                ? confidence
                : (GeofenceEventConfidence?)null;

            var confirmation = new ArrivalConfirmation(
                PickupRegionId(jobId), basis, _clock.GetUtcNow());

            _confirmed[jobId] = confirmation;

            return confirmation;
        }
    }

    ArrivalConfirmation? IJobSiteMonitor.ConfirmationFor(string jobId)
    {
        lock (_sync)
            return _confirmed.GetValueOrDefault(jobId);
    }

    // Every position, every armed region. The only work done here is asking
    // whether fresh evidence has moved against a confirmation we are holding.
    private void OnObserved(object? sender, RegionObservation observation)
    {
        string? contradictedJob = null;
        ArrivalConfirmation? dropped = null;

        lock (_sync)
        {
            foreach (var (jobId, confirmation) in _confirmed)
            {
                if (confirmation.RegionId != observation.RegionId)
                    continue;

                if (ArrivalStanding.Evaluate(confirmation, observation)
                    is not ConfirmationStanding.Contradicted)
                    break;

                contradictedJob = jobId;
                dropped = confirmation;

                break;
            }

            if (contradictedJob is null)
                return;

            _confirmed.Remove(contradictedJob);
            _pickupEntryConfidence.Remove(contradictedJob);

            // _left is untouched: an arrival that never happened cannot have
            // produced a departure
        }

        _arrivalContradicted?.Invoke(
            this, new ArrivalContradictedEventArgs(contradictedJob, dropped!, observation.At));
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

            if (_confirmed.ContainsKey(jobId))
                return;

            // An enter can be re-raised as the evidence for it improves:
            // Suppressed under DR, then LowConfidence once a real fix backs
            // it up. Only ever move up - a later DR re-entry must not
            // downgrade an arrival a trusted fix already established.
            _pickupEntryConfidence[jobId] =
                _pickupEntryConfidence.TryGetValue(jobId, out var existing) && existing > confidence
                    ? existing
                    : confidence;

            confidence = _pickupEntryConfidence[jobId];
        }

        _arrivalAvailable?.Invoke(this, new ArrivalAvailableEventArgs(jobId, confidence));
    }

    private void OnPickupExited(string jobId, GeofenceEventConfidence confidence)
    {
        JobAssignment? job;

        lock (_sync)
        {
            if (!_armed.TryGetValue(jobId, out job))
                return;

            // leaving before the driver confirmed is just driving past; the
            // entry no longer counts as evidence
            if (!_confirmed.ContainsKey(jobId))
            {
                _pickupEntryConfidence.Remove(jobId);

                return;
            }

            if (!_left.Add(jobId))
                return;
        }

        // the pickup has no further role once left
        _registry.Remove(PickupRegionId(jobId));

        // registered only now, so a nearby dropoff can't fire while the
        // driver is still waiting at the pickup
        if (job.Dropoff is { } dropoff)
        {
            RegisterSite(
                DropoffRegionId(jobId),
                dropoff,
                (transition, c) => OnDropoffTransition(jobId, transition, c));
        }

        _siteLeft?.Invoke(this, new SiteLeftEventArgs(jobId, confidence, _clock.GetUtcNow()));
    }

    // TODO: dropoff arrival; a placeholder until its behavior is defined.
    // Note it is the same shape as the pickup - an arrival, a confirmation,
    // a standing check - so it should reuse them rather than grow a parallel
    // set of fields.
    private void OnDropoffTransition(
        string jobId, GeofenceTransition transition, GeofenceEventConfidence confidence)
    {
    }

    private void RegisterSite(
        string regionId,
        JobSite site,
        Action<GeofenceTransition, GeofenceEventConfidence> onTransition,
        double exitRadiusFactor = 1.0) =>
        _registry.Add(new CircularGeofenceRegion(
            id: regionId,
            centerLatitude: site.Latitude,
            centerLongitude: site.Longitude,
            radiusMeters: site.GeofenceRadiusMeters,
            onEvent: (_, transition, confidence) => onTransition(transition, confidence),
            enterDwell: site.EnterDwell ?? SiteDwell,
            exitRadiusMeters: site.GeofenceRadiusMeters * exitRadiusFactor));

    // caller holds _sync
    private void ClearJobState(string jobId)
    {
        _pickupEntryConfidence.Remove(jobId);
        _confirmed.Remove(jobId);
        _left.Remove(jobId);
    }

    private static string PickupRegionId(string jobId) => $"job:{jobId}:pickup";

    private static string DropoffRegionId(string jobId) => $"job:{jobId}:dropoff";
}
