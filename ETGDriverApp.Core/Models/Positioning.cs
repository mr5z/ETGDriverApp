namespace ETGDriverApp.Core.Models;

public enum PositionSourceType
{
    Continuous,
    Forced,
    DeadReckoned
}

public record RawPositionSample(
    double Latitude,
    double Longitude,
    double AccuracyMeters,
    DateTimeOffset Timestamp,
    PositionSourceType SourceType,
    double? SpeedMps = null,
    double? CourseDegrees = null);

public enum PositionState
{
    Tracking,
    Degraded,
    DeadReckoning,
    Reacquiring
}

public record NormalizedPosition(
    double Latitude,
    double Longitude,
    double AccuracyMeters,
    PositionSourceType SourceType,
    DateTimeOffset Timestamp,
    PositionState State,
    double? UncertaintyRadiusMeters = null)
{
    // the radius consumers should draw: reported accuracy for a real fix,
    // the state machine's grown uncertainty while extrapolating
    public double EffectiveRadiusMeters =>
        Math.Max(
            UncertaintyRadiusMeters ?? 0,
            double.IsFinite(AccuracyMeters) ? AccuracyMeters : 0);
}

public enum RejectionReason
{
    None,
    AccuracyBelowThreshold,
    ImplausibleJump,
    OutOfOrderTimestamp
}

public enum AccuracyTier
{
    Rejected,
    Borderline,
    Good
}

public interface IAccuracyGate
{
    AccuracyTier Classify(RawPositionSample sample);
}

public interface ISpeedSanityChecker
{
    double MaxPlausibleSpeedKph { get; }

    bool Accepts(RawPositionSample candidate, NormalizedPosition lastAccepted);
    
    double? ImpliedKph(RawPositionSample candidate, NormalizedPosition lastAccepted);
}

public interface IPositionSmoother
{
    NormalizedPosition Smooth(RawPositionSample accepted, NormalizedPosition? previous);
}

public interface IMapMatcher
{
    Task<NormalizedPosition> SnapToRoadAsync(NormalizedPosition smoothed, CancellationToken ct = default);
}

public interface IPositionBlender
{
    NormalizedPosition Blend(NormalizedPosition gpsDerived, NormalizedPosition deadReckoned, double gpsWeight);
}

public static class Geo
{
    public const double EarthRadiusMeters = 6_371_000;

    public static double ToRad(double deg) => deg * Math.PI / 180.0;

    public static double ToDeg(double rad) => rad * 180.0 / Math.PI;

    public static double NormalizeDegrees(double degrees) => ((degrees % 360) + 360) % 360;

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        var dLon = ToRad(lon2 - lon1);
        var y = Math.Sin(dLon) * Math.Cos(ToRad(lat2));
        var x = Math.Cos(ToRad(lat1)) * Math.Sin(ToRad(lat2)) -
                Math.Sin(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Cos(dLon);

        return NormalizeDegrees(ToDeg(Math.Atan2(y, x)));
    }

    public static (double Lat, double Lon) Project(double lat, double lon, double headingDeg, double distanceM)
    {
        var angularDistance = distanceM / EarthRadiusMeters;
        var heading = ToRad(headingDeg);
        var lat1 = ToRad(lat);
        var lon1 = ToRad(lon);

        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(angularDistance) +
                             Math.Cos(lat1) * Math.Sin(angularDistance) * Math.Cos(heading));

        var lon2 = lon1 + Math.Atan2(Math.Sin(heading) * Math.Sin(angularDistance) * Math.Cos(lat1),
                                     Math.Cos(angularDistance) - Math.Sin(lat1) * Math.Sin(lat2));

        return (ToDeg(lat2), ToDeg(lon2));
    }
}
