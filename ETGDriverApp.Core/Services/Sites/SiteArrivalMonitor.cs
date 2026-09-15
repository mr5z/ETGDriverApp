using ETGDriverApp.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Services.Sites;

// One dictionary, keyed by the same string the geofence registry is keyed by.
//
// The four parallel dictionaries this replaces were all keyed by job while
// the registry was keyed by region, so every observation had to be translated
// back by scanning - which is where the scan that stopped at the first match
// came from. With one key there is nothing to translate and nothing to scan.
internal class SiteArrivalMonitor : ISiteArrivalMonitor, IDisposable
{
    // Everything known about one watched place. Mutable under _sync rather
    // than a record-with: the standing is a small state machine and every
    // transition is made while holding the lock anyway.
    private sealed class SiteWatch(SiteDefinition site)
    {
        public SiteDefinition Site { get; } = site;

        // The evidence an arrival was offered on, and when. Null means no
        // offer stands. Only ever raised while an offer stands - a later
        // dead-reckoned re-entry must not downgrade an arrival a trusted fix
        // established - but cleared outright on a lapse, because a lapse
        // means the evidence was withdrawn, not that weaker evidence arrived.
        public GeofenceEventConfidence? OfferedConfidence { get; set; }

        public DateTimeOffset? OfferedAt { get; set; }

        public ArrivalConfirmation? Confirmation { get; set; }

        // a confirmed arrival produces at most one departure
        public bool Left { get; set; }

        public void ClearStanding()
        {
            OfferedConfidence = null;
            OfferedAt = null;
            Confirmation = null;
            Left = false;
        }
    }

    private readonly IGeofenceRegistry _registry;
    private readonly IGeofenceEvaluator _evaluator;
    private readonly IOptionsMonitor<PositioningOptions> _options;
    private readonly TimeProvider _clock;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, SiteWatch> _watched = [];

    public SiteArrivalMonitor(
        IGeofenceRegistry registry,
        IGeofenceEvaluator evaluator,
        IOptionsMonitor<PositioningOptions> options,
        TimeProvider clock)
    {
        _registry = registry;
        _evaluator = evaluator;
        _options = options;
        _clock = clock;

        // Observations arrive before transitions for the same position, so a
        // confirmation contradicted by this fix is already gone by the time
        // the resulting Exited lands - which is what stops a bogus arrival
        // from looking like a departure.
        _evaluator.Observed += OnObserved;
    }

    private EventHandler<SiteArrivalEventArgs>? _arrivalAvailable;
    event EventHandler<SiteArrivalEventArgs> ISiteArrivalMonitor.ArrivalAvailable
    {
        add => _arrivalAvailable += value;
        remove => _arrivalAvailable -= value;
    }

    private EventHandler<SiteArrivalEventArgs>? _arrivalLapsed;
    event EventHandler<SiteArrivalEventArgs> ISiteArrivalMonitor.ArrivalLapsed
    {
        add => _arrivalLapsed += value;
        remove => _arrivalLapsed -= value;
    }

    private EventHandler<ArrivalContradictedEventArgs>? _arrivalContradicted;
    event EventHandler<ArrivalContradictedEventArgs> ISiteArrivalMonitor.ArrivalContradicted
    {
        add => _arrivalContradicted += value;
        remove => _arrivalContradicted -= value;
    }

    private EventHandler<SiteLeftEventArgs>? _siteLeft;
    event EventHandler<SiteLeftEventArgs> ISiteArrivalMonitor.SiteLeft
    {
        add => _siteLeft += value;
        remove => _siteLeft -= value;
    }

    IReadOnlyList<string> ISiteArrivalMonitor.WatchedSiteKeys
    {
        get
        {
            lock (_sync)
                return [.. _watched.Keys];
        }
    }

    void ISiteArrivalMonitor.Watch(string siteKey, SiteDefinition site)
    {
        var dwell = site.EnterDwell ?? _options.CurrentValue.Geofence.DefaultEnterDwell;

        lock (_sync)
        {
            // a replaced definition invalidates the standing built against
            // the old fence
            _watched[siteKey] = new SiteWatch(site);
        }

        _registry.Add(new CircularGeofenceRegion(
            id: siteKey,
            centerLatitude: site.Latitude,
            centerLongitude: site.Longitude,
            radiusMeters: site.GeofenceRadiusMeters,
            onEvent: (id, transition, confidence) => OnTransition(id, transition, confidence),
            enterDwell: dwell,
            exitRadiusMeters: site.GeofenceRadiusMeters * site.ExitRadiusFactor));
    }

    bool ISiteArrivalMonitor.Unwatch(string siteKey)
    {
        lock (_sync)
            _watched.Remove(siteKey);

        return _registry.Remove(siteKey);
    }

    // Called on session teardown as well as by the caller. A stopped session
    // that kept its fences armed would go on evaluating the last shift's
    // sites - a driver parked at home inside an old radius could still
    // produce an arrival.
    int ISiteArrivalMonitor.UnwatchAll()
    {
        List<string> keys;

        lock (_sync)
        {
            keys = [.. _watched.Keys];
            _watched.Clear();
        }

        foreach (var key in keys)
            _registry.Remove(key);

        return keys.Count;
    }

    ArrivalConfirmation? ISiteArrivalMonitor.Confirm(string siteKey)
    {
        lock (_sync)
        {
            if (!_watched.TryGetValue(siteKey, out var watch))
                return null;

            // a double tap is a no-op, and returns nothing rather than a
            // second confirmation with a later timestamp
            if (watch.Confirmation is not null)
                return null;

            var confirmation = new ArrivalConfirmation(
                siteKey, watch.OfferedConfidence, _clock.GetUtcNow());

            watch.Confirmation = confirmation;

            return confirmation;
        }
    }

    ArrivalConfirmation? ISiteArrivalMonitor.ConfirmationFor(string siteKey)
    {
        lock (_sync)
            return _watched.GetValueOrDefault(siteKey)?.Confirmation;
    }

    void IDisposable.Dispose() => _evaluator.Observed -= OnObserved;

    // Every position, every watched site. The only work done here is asking
    // whether fresh evidence has moved against a belief we are holding -
    // either a confirmation the driver made, or an offer we made to them.
    private void OnObserved(object? sender, RegionObservation observation)
    {
        ArrivalConfirmation? dropped;
        GeofenceEventConfidence? lapsed;

        // The lock decides and mutates; it does not return. Every exit path
        // falls through to the dispatch below, so there is no way to drop a
        // belief silently by adding an early return later.
        lock (_sync)
            (dropped, lapsed) = JudgeAgainst(observation);

        if (dropped is { } d)
            _arrivalContradicted?.Invoke(
                this, new ArrivalContradictedEventArgs(observation.SiteKey, d, observation.At));

        if (lapsed is { } l)
            _arrivalLapsed?.Invoke(
                this, new SiteArrivalEventArgs(observation.SiteKey, l, observation.At));
    }

    // caller holds _sync. Returns what the caller must announce, if anything.
    private (ArrivalConfirmation? Dropped, GeofenceEventConfidence? Lapsed) JudgeAgainst(
        RegionObservation observation)
    {
        // one lookup; no scan, so no way to stop at the wrong entry
        if (!_watched.TryGetValue(observation.SiteKey, out var watch))
            return (null, null);

        if (watch.Confirmation is { } confirmation)
        {
            if (ArrivalStanding.Evaluate(confirmation, observation)
                is not ConfirmationStanding.Contradicted)
                return (null, null);

            // The offer goes with it: evidence that contradicts the
            // confirmation contradicts the offer it rested on.
            //
            // Left is untouched. An arrival that never happened cannot have
            // produced a departure.
            watch.Confirmation = null;
            watch.OfferedConfidence = null;
            watch.OfferedAt = null;

            // Only the contradiction is announced. A lapse alongside it would
            // be a second event about the same fact, and the caller already
            // has to clear the offer on a contradiction.
            return (confirmation, null);
        }

        if (watch.OfferedConfidence is not { } offered)
            return (null, null);

        // An offer is judged by exactly the test a confirmation is, so the
        // two cannot drift apart. Standing in for the human who has not
        // answered yet.
        var standing = ArrivalStanding.Evaluate(
            new ArrivalConfirmation(observation.SiteKey, offered, watch.OfferedAt!.Value),
            observation);

        if (standing is not ConfirmationStanding.Contradicted)
            return (null, null);

        watch.OfferedConfidence = null;
        watch.OfferedAt = null;

        return (null, offered);
    }

    private void OnTransition(
        string siteKey, GeofenceTransition transition, GeofenceEventConfidence confidence)
    {
        switch (transition)
        {
            case GeofenceTransition.Entered:
                OnEntered(siteKey, confidence);
                break;

            case GeofenceTransition.Exited:
                OnLeft(siteKey, confidence);
                break;
        }
    }

    private void OnEntered(string siteKey, GeofenceEventConfidence confidence)
    {
        lock (_sync)
        {
            if (!_watched.TryGetValue(siteKey, out var watch))
                return;

            // the driver has already answered; a better fix does not need to
            // re-ask them
            if (watch.Confirmation is not null)
                return;

            // An enter can be re-raised as the evidence for it improves:
            // Suppressed under dead reckoning, then LowConfidence once a real
            // fix backs it up. Only ever move up.
            if (watch.OfferedConfidence is { } existing && existing >= confidence)
                confidence = existing;

            watch.OfferedConfidence = confidence;
            watch.OfferedAt ??= _clock.GetUtcNow();
        }

        _arrivalAvailable?.Invoke(
            this, new SiteArrivalEventArgs(siteKey, confidence, _clock.GetUtcNow()));
    }

    private void OnLeft(string siteKey, GeofenceEventConfidence confidence)
    {
        GeofenceEventConfidence? lapsed = null;
        var left = false;

        lock (_sync)
        {
            if (!_watched.TryGetValue(siteKey, out var watch))
                return;

            if (watch.Confirmation is null)
            {
                // leaving before the driver answered is just driving past;
                // the entry no longer counts as evidence
                lapsed = watch.OfferedConfidence;

                watch.OfferedConfidence = null;
                watch.OfferedAt = null;
            }
            else if (!watch.Left)
            {
                watch.Left = true;
                left = true;
            }
        }

        var now = _clock.GetUtcNow();

        // A drive-past is a lapse like any other. The caller hears one event
        // for "stop offering this", whatever the reason, rather than having
        // to infer it from a departure it may not care about.
        if (lapsed is { } l)
            _arrivalLapsed?.Invoke(this, new SiteArrivalEventArgs(siteKey, l, now));

        // The site stays watched. Whether it still has a role - and whether
        // some other site should be watched now - is the caller's to decide.
        if (left)
            _siteLeft?.Invoke(this, new SiteLeftEventArgs(siteKey, confidence, now));
    }
}