using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Helpers;
using ETGDriverApp.Core.Models;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Services.Filters;

// Two reference frames, chosen by whether the filter currently holds
// anything worth judging against.
//
//   Innovation frame - the normal case. The filter has been fed real fixes,
//   so its prediction and covariance are meaningful and a sample is judged
//   in sigma. Delegates to IPlausibilityGate, which is unchanged.
//
//   Reachability frame - while extrapolating. The filter holds only DR
//   output, so innovation is circular. The frame is instead the last
//   published position and the furthest the vehicle could have travelled
//   while we were blind. A sample inside that is adopted; one outside is
//   held until a second sample agrees with it.
//
// Stateful by design: the held candidate has to survive between calls. One
// session runs at a time, and the candidate self-expires, so there is no
// reset to forget to call.
internal class AdmissionGate(
    IPlausibilityGate plausibility,
    IOptionsMonitor<PositioningOptions> options) : IAdmissionGate
{
    private RawPositionSample? _candidate;

    AdmissionVerdict IAdmissionGate.Evaluate(AdmissionContext context)
    {
        var sample = context.Sample;

        // Nothing to judge against at all. A cached fix is allowed here and
        // only here: knowing roughly where we were beats knowing nothing,
        // and its own old timestamp keeps the blind timer honest.
        if (!context.Filter.IsInitialized)
        {
            // an extrapolation has no anchor of its own to offer
            return sample.IsDeadReckoned
                ? AdmissionVerdict.Reject(RejectionReason.None)
                : AdmissionVerdict.Reseed();
        }

        return context.Extrapolating && !sample.IsDeadReckoned
            ? Reachability(sample, context.Published)
            : Innovation(sample, context.Filter);
    }

    private AdmissionVerdict Innovation(RawPositionSample sample, PositionKalmanFilter filter)
    {
        // the track is continuous again, so a held candidate is stale
        _candidate = null;

        var verdict = plausibility.Evaluate(sample, filter);

        return verdict.Accepted
            ? AdmissionVerdict.Update()
            : AdmissionVerdict.Reject(RejectionReason.ImplausibleJump, verdict.Describe());
    }

    private AdmissionVerdict Reachability(RawPositionSample sample, NormalizedPosition? published)
    {
        // A cached fix is evidence about the past. Ending a dead-reckoning
        // episode with one would rewind the track to wherever the device
        // last managed a fix - which, during exactly the outage DR exists to
        // cover, is the least useful position available.
        if (sample.IsCached)
            return AdmissionVerdict.Reject(RejectionReason.CachedFixNotAnObservation);

        if (published is not { } anchor)
            return AdmissionVerdict.Reseed();

        var opts = options.CurrentValue;

        // the furthest the vehicle could have got while blind, widened by the
        // error each end of the comparison carries
        var reachableMeters =
            opts.Plausibility.MaxPlausibleSpeedKph * Units.KphToMps * opts.State.MaxBlindSeconds
            + anchor.EffectiveRadiusMeters
            + (Accuracy.IsKnown(sample.AccuracyMeters) ? sample.AccuracyMeters : 0);

        var jumpedMeters = Geo.DistanceMeters(
            anchor.Latitude, anchor.Longitude, sample.Latitude, sample.Longitude);

        if (jumpedMeters <= reachableMeters)
        {
            _candidate = null;

            return AdmissionVerdict.Reseed();
        }

        if (ConfirmedBy(sample, opts))
        {
            _candidate = null;

            return AdmissionVerdict.Reseed();
        }

        _candidate = sample;

        return AdmissionVerdict.Hold(
            RejectionReason.UnconfirmedRelocation,
            $"jumped={jumpedMeters:F0} reachable={reachableMeters:F0}");
    }

    // The confirmation has to agree with the candidate in position and be
    // close enough in time to belong to the same episode. Without the time
    // bound a candidate could sit for hours waiting for a coincidence.
    private bool ConfirmedBy(RawPositionSample sample, PositioningOptions opts)
    {
        if (_candidate is not { } candidate)
            return false;

        if (sample.Timestamp - candidate.Timestamp > opts.Staleness.HardThreshold)
            return false;

        var toleranceMeters =
            (Accuracy.IsKnown(candidate.AccuracyMeters) ? candidate.AccuracyMeters : 0) +
            (Accuracy.IsKnown(sample.AccuracyMeters) ? sample.AccuracyMeters : 0);

        return Geo.DistanceMeters(
            candidate.Latitude, candidate.Longitude, sample.Latitude, sample.Longitude)
            <= toleranceMeters;
    }
}