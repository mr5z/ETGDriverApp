using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Models;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Services.Filters;

// Innovation gating against the filter's own uncertainty, plus an absurdity
// ceiling that holds regardless of what the filter believes.
internal interface IPlausibilityGate
{
    PlausibilityVerdict Evaluate(RawPositionSample sample, PositionKalmanFilter filter);
}

// Replaces `bool Accepts(..., out string diagnostics)`. The out-string forced
// every caller to format a message even on the accept path, and made the
// reason for a rejection a matter of reading prose.
internal readonly record struct PlausibilityVerdict(
    bool Accepted,
    double InnovationSigma,
    double FilterSpeedKph,
    PlausibilityFailure Failure)
{
    public string Describe() =>
        $"sigma={InnovationSigma:F1} filterKph={FilterSpeedKph:F0} failure={Failure}";
}

internal enum PlausibilityFailure
{
    None,
    ImplausibleSpeed,
    InnovationTooLarge
}

internal class PlausibilityGate(IOptionsMonitor<PositioningOptions> options) : IPlausibilityGate
{
    PlausibilityVerdict IPlausibilityGate.Evaluate(
        RawPositionSample sample, PositionKalmanFilter filter)
    {
        var limits = options.CurrentValue.Plausibility;

        var sigma = filter.PositionInnovationSigma(
            sample.Latitude, sample.Longitude, sample.AccuracyMeters);

        // the filter's velocity state, not a difference of positions
        var impliedKph = filter.SpeedMps * Units.MpsToKph;

        var failure = impliedKph > limits.MaxPlausibleSpeedKph
            ? PlausibilityFailure.ImplausibleSpeed
            : sigma > limits.MaxInnovationSigma
                ? PlausibilityFailure.InnovationTooLarge
                : PlausibilityFailure.None;

        return new PlausibilityVerdict(
            failure == PlausibilityFailure.None, sigma, impliedKph, failure);
    }
}
