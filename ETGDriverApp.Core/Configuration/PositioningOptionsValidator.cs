using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Configuration;

// Runs on every bind, including every reload from the future remote source.
//
// Deliberately fails the whole payload rather than silently repairing it:
// IOptionsMonitor keeps serving the last value that validated, so a bad
// server push degrades to "we keep running on the previous config" instead
// of "positioning quietly behaves differently in the field". The hard bounds
// below are the safety envelope; they are NOT tuning knobs and are not
// configurable by design.
public sealed class PositioningOptionsValidator : IValidateOptions<PositioningOptions>
{
    public ValidateOptionsResult Validate(string? name, PositioningOptions o)
    {
        List<string> failures = [];

        Range(failures, "AccuracyGate.RejectAboveMeters", o.AccuracyGate.RejectAboveMeters, 5, 500);
        Range(failures, "AccuracyGate.DegradedBandMeters", o.AccuracyGate.DegradedBandMeters, 0, 500);

        if (o.AccuracyGate.DegradedBandMeters >= o.AccuracyGate.RejectAboveMeters)
            failures.Add("AccuracyGate.DegradedBandMeters must be below RejectAboveMeters.");

        Range(failures, "Plausibility.MaxInnovationSigma", o.Plausibility.MaxInnovationSigma, 1, 100);
        Range(failures, "Plausibility.MaxPlausibleSpeedKph", o.Plausibility.MaxPlausibleSpeedKph, 20, 1000);

        Range(failures, "Filter.AccelNoiseMps2", o.Filter.AccelNoiseMps2, 0.05, 20);
        Range(failures, "Filter.InitialVelocityVarianceM2PerS2", o.Filter.InitialVelocityVarianceM2PerS2, 1, 10_000);
        Range(failures, "Filter.ReportedSpeedAccuracyMps", o.Filter.ReportedSpeedAccuracyMps, 0.1, 50);

        Range(failures, "DeadReckoning.BaseAccuracyMeters", o.DeadReckoning.BaseAccuracyMeters, 1, 500);
        Range(failures, "DeadReckoning.HeadingDriftDegPerSec", o.DeadReckoning.HeadingDriftDegPerSec, 0, 30);
        Range(failures, "DeadReckoning.SpeedErrorFraction", o.DeadReckoning.SpeedErrorFraction, 0, 1);
        Range(failures, "DeadReckoning.MinTrustworthyFixErrorMeters", o.DeadReckoning.MinTrustworthyFixErrorMeters, 1, 200);
        Range(failures, "DeadReckoning.BiasGain", o.DeadReckoning.BiasGain, 0, 1);
        Range(failures, "DeadReckoning.MaxBiasDegPerSec", o.DeadReckoning.MaxBiasDegPerSec, 0, 30);
        Range(failures, "DeadReckoning.MinRecalibrationIntervalSeconds", o.DeadReckoning.MinRecalibrationIntervalSeconds, 0.05, 60);
        Range(failures, "DeadReckoning.TickIntervalSeconds", o.DeadReckoning.TickIntervalSeconds, 0.1, 60);
        Range(failures, "DeadReckoning.MaxBiasSpanSeconds", o.DeadReckoning.MaxBiasSpanSeconds, 1, 600);

        Range(failures, "State.MaxUsefulUncertaintyMeters", o.State.MaxUsefulUncertaintyMeters, 10, 5_000);
        Range(failures, "State.ReacquisitionSettleSeconds", o.State.ReacquisitionSettleSeconds, 0, 120);
        Range(failures, "State.MaxBlindSeconds", o.State.MaxBlindSeconds, 5, 3_600);

        Range(failures, "Staleness.SoftThresholdSeconds", o.Staleness.SoftThresholdSeconds, 2, 600);
        Range(failures, "Staleness.HardThresholdSeconds", o.Staleness.HardThresholdSeconds, 3, 1_200);
        Range(failures, "Staleness.TickIntervalSeconds", o.Staleness.TickIntervalSeconds, 0.5, 60);
        Range(failures, "Staleness.MinTimeBetweenForcedFixesSeconds", o.Staleness.MinTimeBetweenForcedFixesSeconds, 1, 600);

        // the escalation order is the contract the watchdog is written against
        if (o.Staleness.HardThresholdSeconds <= o.Staleness.SoftThresholdSeconds)
            failures.Add("Staleness.HardThresholdSeconds must exceed SoftThresholdSeconds.");

        // the watchdog can only notice staleness on a tick
        if (o.Staleness.TickIntervalSeconds > o.Staleness.SoftThresholdSeconds)
            failures.Add("Staleness.TickIntervalSeconds must not exceed SoftThresholdSeconds.");

        // giving up before the first stale notification would be unreachable
        if (o.State.MaxBlindSeconds <= o.Staleness.HardThresholdSeconds)
            failures.Add("State.MaxBlindSeconds must exceed Staleness.HardThresholdSeconds.");

        Range(failures, "Store.RetentionHours", o.Store.RetentionHours, 0.5, 720);
        Range(failures, "Store.PruneEveryWrites", o.Store.PruneEveryWrites, 1, 100_000);

        Range(failures, "Recovery.MaxUsefulContinuityMinutes", o.Recovery.MaxUsefulContinuityMinutes, 0.5, 720);
        Range(failures, "Recovery.RecoveryWindowHours", o.Recovery.RecoveryWindowHours, 0.5, 720);

        // recovering a window we no longer retain returns a truncated track
        if (o.Recovery.RecoveryWindowHours > o.Store.RetentionHours)
            failures.Add("Recovery.RecoveryWindowHours must not exceed Store.RetentionHours.");

        Range(failures, "Geofence.WellInsideRadiusMultiplier", o.Geofence.WellInsideRadiusMultiplier, 1, 50);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Range(List<string> failures, string key, double value, double min, double max)
    {
        if (double.IsNaN(value) || value < min || value > max)
            failures.Add($"{key} must be between {min} and {max} (was {value}).");
    }
}
