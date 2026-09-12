using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Models;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Services.Filters;

// Constant-velocity filter over [x, y, vx, vy] in metres and metres/sec,
// where x is east and y is north of a fixed session origin. Degrees are
// converted on the way in and back out; nothing outside this class sees
// the local frame.
internal class PositionKalmanFilter(IOptionsMonitor<PositioningOptions> options)
{
    // Numerical floors, not tuning knobs: they exist so a measurement that
    // claims zero error cannot drive the gain to a singularity. Deliberately
    // NOT configurable - there is no operational reason to move them, and a
    // remote value of 0 would make the filter blow up.
    private const double MinPositionVarianceM2 = 1;
    private const double MinVelocityVarianceM2PerS2 = 0.25;

    // below this the velocity direction is noise, not a course
    private const double MinCourseSpeedMps = 0.5;

    private double _originLat;
    private double _originLon;
    private double _metresPerDegreeLat;
    private double _metresPerDegreeLon;
    private bool _initialized;

    // state
    private double _x, _y, _vx, _vy;

    // covariance, row-major 4x4
    private readonly double[,] _p = new double[4, 4];

    private DateTimeOffset _lastUpdate;

    public bool IsInitialized => _initialized;

    public (double Latitude, double Longitude) Position => ToGeographic(_x, _y);

    public double SpeedMps => Math.Sqrt(_vx * _vx + _vy * _vy);

    public double? CourseDegrees => SpeedMps < MinCourseSpeedMps
        ? null
        : Geo.NormalizeDegrees(Geo.ToDeg(Math.Atan2(_vx, _vy)));

    // one-sigma horizontal position uncertainty
    public double PositionUncertaintyMeters => Math.Sqrt(Math.Max(_p[0, 0] + _p[1, 1], 0) / 2);

    public void Initialize(
        double latitude, double longitude, double accuracyMeters, DateTimeOffset timestamp)
    {
        var filter = options.CurrentValue.Filter;

        _originLat = latitude;
        _originLon = longitude;
        _metresPerDegreeLat = Geo.EarthRadiusMeters * Math.PI / 180;
        _metresPerDegreeLon = _metresPerDegreeLat * Math.Cos(Geo.ToRad(latitude));

        _x = 0;
        _y = 0;
        _vx = 0;
        _vy = 0;

        var variance = Math.Max(accuracyMeters * accuracyMeters, MinPositionVarianceM2);

        Array.Clear(_p);
        _p[0, 0] = variance;
        _p[1, 1] = variance;

        // no velocity information yet, so start wide
        _p[2, 2] = filter.InitialVelocityVarianceM2PerS2;
        _p[3, 3] = filter.InitialVelocityVarianceM2PerS2;

        _lastUpdate = timestamp;
        _initialized = true;
    }

    public void Predict(DateTimeOffset now)
    {
        var dt = (now - _lastUpdate).TotalSeconds;

        if (dt <= 0)
            return;

        _lastUpdate = now;

        // x += vx*dt
        _x += _vx * dt;
        _y += _vy * dt;

        PredictCovariance(dt, options.CurrentValue.Filter.AccelNoiseMps2);
    }

    // returns the normalized innovation: how many sigma the measurement sits
    // from the prediction. Caller decides whether to accept it.
    public double PositionInnovationSigma(
        double latitude, double longitude, double accuracyMeters)
    {
        var (mx, my) = ToLocal(latitude, longitude);

        var dx = mx - _x;
        var dy = my - _y;

        var r = Math.Max(accuracyMeters * accuracyMeters, MinPositionVarianceM2);

        var sx = _p[0, 0] + r;
        var sy = _p[1, 1] + r;

        return Math.Sqrt(dx * dx / sx + dy * dy / sy);
    }

    public void UpdatePosition(double latitude, double longitude, double accuracyMeters)
    {
        var (mx, my) = ToLocal(latitude, longitude);
        var r = Math.Max(accuracyMeters * accuracyMeters, MinPositionVarianceM2);

        UpdateScalar(index: 0, measurement: mx, r);
        UpdateScalar(index: 1, measurement: my, r);
    }

    public void UpdateVelocity(double speedMps, double courseDegrees, double speedAccuracyMps)
    {
        var course = Geo.ToRad(courseDegrees);

        var vx = speedMps * Math.Sin(course);
        var vy = speedMps * Math.Cos(course);

        var r = Math.Max(speedAccuracyMps * speedAccuracyMps, MinVelocityVarianceM2PerS2);

        UpdateScalar(index: 2, measurement: vx, r);
        UpdateScalar(index: 3, measurement: vy, r);
    }

    private void PredictCovariance(double dt, double accelNoiseMps2)
    {
        // P = F P F' + Q, with F the constant-velocity transition
        var dt2 = dt * dt;

        // F P F' for the block-diagonal CV model, done in place per axis
        for (var axis = 0; axis < 2; axis++)
        {
            var p = axis;      // position index
            var v = axis + 2;  // velocity index

            var pp = _p[p, p] + dt * (_p[p, v] + _p[v, p]) + dt2 * _p[v, v];
            var pv = _p[p, v] + dt * _p[v, v];
            var vp = _p[v, p] + dt * _p[v, v];
            var vv = _p[v, v];

            // Q for a discrete white-noise acceleration model
            var q = accelNoiseMps2 * accelNoiseMps2;

            _p[p, p] = pp + q * dt2 * dt2 / 4;
            _p[p, v] = pv + q * dt2 * dt / 2;
            _p[v, p] = vp + q * dt2 * dt / 2;
            _p[v, v] = vv + q * dt2;
        }
    }

    // scalar measurement of a single state element; the measurement matrix is
    // a unit row, so the gain reduces to a column of P over (P[i,i] + r)
    private void UpdateScalar(int index, double measurement, double r)
    {
        var innovation = measurement - StateAt(index);
        var s = _p[index, index] + r;

        if (s <= 0)
            return;

        Span<double> gain = stackalloc double[4];

        for (var i = 0; i < 4; i++)
            gain[i] = _p[i, index] / s;

        SetState(0, StateAt(0) + gain[0] * innovation);
        SetState(1, StateAt(1) + gain[1] * innovation);
        SetState(2, StateAt(2) + gain[2] * innovation);
        SetState(3, StateAt(3) + gain[3] * innovation);

        // P = (I - K H) P
        var row = new double[4];

        for (var j = 0; j < 4; j++)
            row[j] = _p[index, j];

        for (var i = 0; i < 4; i++)
        for (var j = 0; j < 4; j++)
            _p[i, j] -= gain[i] * row[j];
    }

    private double StateAt(int index) => index switch
    {
        0 => _x,
        1 => _y,
        2 => _vx,
        _ => _vy
    };

    private void SetState(int index, double value)
    {
        switch (index)
        {
            case 0: _x = value; break;
            case 1: _y = value; break;
            case 2: _vx = value; break;
            default: _vy = value; break;
        }
    }

    private (double X, double Y) ToLocal(double latitude, double longitude) => (
        (longitude - _originLon) * _metresPerDegreeLon,
        (latitude - _originLat) * _metresPerDegreeLat);

    private (double Latitude, double Longitude) ToGeographic(double x, double y) => (
        _originLat + y / _metresPerDegreeLat,
        _originLon + x / _metresPerDegreeLon);
}
