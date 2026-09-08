using ETGDriverApp.Core.Models;

namespace ETGDriverApp.Core.Services.Filters;

// Innovation gating against the filter's own uncertainty, plus an absurdity
// ceiling that holds regardless of what the filter believes.
internal interface IPlausibilityGate
{
    bool Accepts(RawPositionSample sample, PositionKalmanFilter filter, out string diagnostics);
}

internal class PlausibilityGate(
    double maxSigma = 5,
    double maxPlausibleSpeedKph = 300) : IPlausibilityGate
{
    bool IPlausibilityGate.Accepts(
        RawPositionSample sample, PositionKalmanFilter filter, out string diagnostics)
    {
        var sigma = filter.PositionInnovationSigma(
            sample.Latitude, sample.Longitude, sample.AccuracyMeters);

        // the filter's velocity state, not a difference of positions
        var impliedKph = filter.SpeedMps * 3.6;

        diagnostics = $"sigma={sigma:F1} filterKph={impliedKph:F0}";

        if (impliedKph > maxPlausibleSpeedKph)
            return false;

        return sigma <= maxSigma;
    }
}