namespace ETGDriverApp.Core.Models;

public static class Geo
{
    public const double EarthRadiusMeters = 6_371_000;

    public static double ToRad(double deg) => deg * Math.PI / 180.0;

    public static double ToDeg(double rad) => rad * 180.0 / Math.PI;

    // C#'s % is a remainder: it keeps the sign of the dividend, so -1 % 360 is
    // -1, not 359. Every angle helper here wants the mathematical modulo.
    private static double Mod(double value, double modulus)
    {
        var remainder = value % modulus;

        return remainder < 0 ? remainder + modulus : remainder;
    }

    public static double NormalizeDegrees(double degrees) => Mod(degrees, 360);

    // Shortest signed turn, in [-180, 180). Shift by half a turn so the wrap
    // point sits at the antipode, take the modulo, shift back.
    public static double SignedDelta(double fromDegrees, double toDegrees) =>
        Mod(toDegrees - fromDegrees + 180, 360) - 180;

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

    public static (double Lat, double Lon) Project(
        double lat, double lon, double headingDeg, double distanceM)
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
