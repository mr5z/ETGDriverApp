namespace ETGDriverApp.Core.Configuration;

// Bound from IConfiguration section "Positioning".
//
// Design notes for the future remote-sync source:
//
//  * Everything here is a plain settable POCO with a usable default, so the
//    app runs correctly with no configuration file and no network at all.
//    A remote provider supplies a partial overlay; anything it omits keeps
//    the compiled-in default.
//  * Durations are expressed as numbers of seconds/hours rather than
//    TimeSpan strings. A server payload should not have to agree with
//    .NET's TimeSpan parsing rules, and a number is trivially clamped.
//    The TimeSpan projections below are the only thing services touch.
//  * Every property name carries its unit. There is no way to read this
//    config and be wrong about metres vs feet or seconds vs milliseconds.
//  * Services take IOptionsMonitor<PositioningOptions> and snapshot
//    CurrentValue ONCE at the top of an operation. Never read it field by
//    field mid-calculation: a reload between two reads would produce an
//    internally inconsistent tick.
//  * Revision is echoed into diagnostics so a misbehaving unit can be traced
//    back to the exact config payload it was running.
//  * The compiled defaults ARE the configuration for any host that does not
//    ship an appsettings.json. Keep them equal to the values under test;
//    a JSON file that silently doubles a threshold is the same bug as a bad
//    remote push, arriving by a different route.
public sealed class PositioningOptions
{
    public const string SectionName = "Positioning";

    // set by the remote provider; "local-default" means nothing was pushed
    public string Revision { get; set; } = "local-default";

    public AccuracyGateOptions AccuracyGate { get; set; } = new();

    public PlausibilityOptions Plausibility { get; set; } = new();

    public KalmanFilterOptions Filter { get; set; } = new();

    public DeadReckoningOptions DeadReckoning { get; set; } = new();

    public PositionStateOptions State { get; set; } = new();

    public StalenessOptions Staleness { get; set; } = new();

    public PositionStoreOptions Store { get; set; } = new();

    public SessionRecoveryOptions Recovery { get; set; } = new();

    public GeofenceOptions Geofence { get; set; } = new();
}

public sealed class AccuracyGateOptions
{
    // worse than this and the fix is not used at all
    public double RejectAboveMeters { get; set; } = 50;

    // the band immediately below the reject threshold classified Borderline
    public double DegradedBandMeters { get; set; } = 20;
}

public sealed class PlausibilityOptions
{
    // how many sigma of the filter's own uncertainty a fix may sit from the
    // prediction before it is treated as a jump
    public double MaxInnovationSigma { get; set; } = 5;

    // Absurdity ceiling that holds regardless of what the filter believes.
    //
    // Also the speed term in AdmissionGate's reachability frame: this times
    // State.MaxBlindSeconds is how far a single unconfirmed fix is allowed
    // to move the track while extrapolating.
    public double MaxPlausibleSpeedKph { get; set; } = 300;
}

public sealed class KalmanFilterOptions
{
    // Acceleration uncertainty: how hard the vehicle can plausibly change
    // velocity between updates.
    //
    // Sizes covariance growth between updates as roughly
    // 0.5 * AccelNoiseMps2 * t^2.
    //
    // NOTE: an earlier version of this comment claimed the free growth stops
    // once dead reckoning engages, because each DR estimate updates the
    // filter and pins uncertainty near DR's claimed accuracy. That is wrong.
    // UpdatePosition uses r = accuracy^2, so a DR sample claiming 400 m
    // arrives with r = 160,000 against a P of a few thousand - the gain is
    // near zero and the update barely moves the covariance. There is one
    // regime, not two, and it grows throughout an outage.
    //
    // 1.5 was high for a road vehicle: the filter reached several hundred
    // metres of uncertainty within half a minute of a quiet stretch. 0.7 is
    // a more honest figure and leaves the escalation path room to work.
    public double AccelNoiseMps2 { get; set; } = 0.7;

    // no velocity information at initialization, so start wide
    public double InitialVelocityVarianceM2PerS2 { get; set; } = 100;

    // assumed accuracy of a platform-reported speed/course pair
    public double ReportedSpeedAccuracyMps { get; set; } = 1.0;

    // Assumed accuracy of a zero-velocity update, applied when dead
    // reckoning reports the vehicle stationary during an outage.
    //
    // Tighter than ReportedSpeedAccuracyMps on purpose. "The accelerometer
    // says we are not moving" is a stronger statement than any GPS-derived
    // speed, and it has to be strong enough to overcome a coasting velocity
    // state that the DR position update - arriving with hundreds of metres
    // of claimed accuracy - cannot touch.
    //
    // Do not widen this to "be safe": a loose value reintroduces the
    // coasting, because the filter simply ignores the stop.
    public double StationaryUpdateAccuracyMps { get; set; } = 0.5;
}

public sealed class DeadReckoningOptions
{
    // floor on the accuracy of an extrapolation, even one second in
    public double BaseAccuracyMeters { get; set; } = 30;

    // Assumed heading error growth, used to size the drift ellipse.
    //
    // Known to be pessimistic: against the simulated vehicle DR's true error
    // ran about a third of what DriftAccuracyMeters claimed. That is the safe
    // direction to be wrong in, and it is also what decides how long a
    // suppressed enter stays offerable - see
    // Geofence.MaxUnverifiedRadiusMultiplier, which compares this radius
    // against the fence. Tune the two together, not separately.
    public double HeadingDriftDegPerSec { get; set; } = 0.5;

    // assumed fractional error on the held speed
    public double SpeedErrorFraction { get; set; } = 0.1;

    // a fix better than this is still treated as having this much error,
    // so recalibration never trusts a bearing over a trivial distance
    public double MinTrustworthyFixErrorMeters { get; set; } = 10;

    // integral gain for the gyro bias estimate, and a sanity cap on it
    public double BiasGain { get; set; } = 0.1;
    public double MaxBiasDegPerSec { get; set; } = 2;

    // a recalibration closer together than this has no usable baseline
    public double MinRecalibrationIntervalSeconds { get; set; } = 0.5;

    public double TickIntervalSeconds { get; set; } = 1;

    // offset changes over a longer span include outage catch-up, not just
    // bias, so they must not be fed to the bias estimator
    public double MaxBiasSpanSeconds { get; set; } = 30;

    public TimeSpan TickInterval => TimeSpan.FromSeconds(TickIntervalSeconds);

    public TimeSpan MaxBiasSpan => TimeSpan.FromSeconds(MaxBiasSpanSeconds);

    public TimeSpan MinRecalibrationInterval =>
        TimeSpan.FromSeconds(MinRecalibrationIntervalSeconds);
}

public sealed class PositionStateOptions
{
    // Beyond this the position is too vague to act on.
    //
    // This is NOT checked against any site's fence radius - it cannot be,
    // since sites arrive at runtime. Geofence.MaxUnverifiedRadiusMultiplier
    // is where that comparison actually happens, per site, at the moment it
    // matters.
    public double MaxUsefulUncertaintyMeters { get; set; } = 150;

    // a fix lands but the state stays Reacquiring for this long
    public double ReacquisitionSettleSeconds { get; set; } = 5;

    // However confident the filter is, a position no real fix has touched in
    // this long cannot be defended.
    //
    // Also the CEILING on AdmissionGate's reachability frame: this times
    // Plausibility.MaxPlausibleSpeedKph bounds how far a single unconfirmed
    // fix may relocate the track. The gate scales down to the time actually
    // blind, so this only binds during a full-length outage.
    public double MaxBlindSeconds { get; set; } = 90;

    public TimeSpan ReacquisitionSettleDuration =>
        TimeSpan.FromSeconds(ReacquisitionSettleSeconds);

    public TimeSpan MaxBlindDuration => TimeSpan.FromSeconds(MaxBlindSeconds);
}

public sealed class StalenessOptions
{
    // no fix for this long: ask the platform for one
    public double SoftThresholdSeconds { get; set; } = 12;

    // no fix for this long: tell the state machine we are blind.
    // PositionStateMachine reads this same value - it used to keep a private
    // copy that a comment asked you to keep in sync by hand.
    public double HardThresholdSeconds { get; set; } = 20;

    public double TickIntervalSeconds { get; set; } = 2;

    // forced fixes are expensive; never more often than this
    public double MinTimeBetweenForcedFixesSeconds { get; set; } = 8;

    public TimeSpan SoftThreshold => TimeSpan.FromSeconds(SoftThresholdSeconds);

    public TimeSpan HardThreshold => TimeSpan.FromSeconds(HardThresholdSeconds);

    public TimeSpan TickInterval => TimeSpan.FromSeconds(TickIntervalSeconds);

    public TimeSpan MinTimeBetweenForcedFixes =>
        TimeSpan.FromSeconds(MinTimeBetweenForcedFixesSeconds);
}

public sealed class PositionStoreOptions
{
    public double RetentionHours { get; set; } = 24;

    public int PruneEveryWrites { get; set; } = 500;

    public TimeSpan Retention => TimeSpan.FromHours(RetentionHours);
}

public sealed class SessionRecoveryOptions
{
    // a gap longer than this means the persisted position is not worth
    // seeding the filter with
    public double MaxUsefulContinuityMinutes { get; set; } = 5;

    // how much history to hand back to the caller after a restart
    public double RecoveryWindowHours { get; set; } = 12;

    public TimeSpan MaxUsefulContinuity =>
        TimeSpan.FromMinutes(MaxUsefulContinuityMinutes);

    public TimeSpan RecoveryWindow => TimeSpan.FromHours(RecoveryWindowHours);
}

public sealed class GeofenceOptions
{
    // A trusted fix this many error-radii inside the region satisfies the
    // dwell immediately: a vehicle driving past is never this far in with
    // this little uncertainty.
    public double WellInsideRadiusMultiplier { get; set; } = 2;

    // An unverified (dead-reckoned) enter is only worth offering while our
    // claimed error is still commensurate with the fence. Beyond this
    // multiple of the site's radius, "inside" carries no information and the
    // offer is withheld.
    //
    // This is the one place DR error and fence radius meet in a comparison.
    // Nothing else in the system relates them, because nothing else can:
    // sites arrive at runtime and no validator sees them.
    public double MaxUnverifiedRadiusMultiplier { get; set; } = 2;

    // Dwell for a site that does not specify its own. Was a private const in
    // the old job monitor - the only arrival-relevant threshold that was not
    // tunable, in a config system built for tuning.
    public double DefaultEnterDwellSeconds { get; set; } = 30;

    public TimeSpan DefaultEnterDwell => TimeSpan.FromSeconds(DefaultEnterDwellSeconds);
}
