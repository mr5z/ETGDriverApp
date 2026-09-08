using Microsoft.Maui.Devices.Sensors;

namespace ETGDriverApp.Core.Services.DeadReckoning.Sensors;

internal class GyroscopeHeadingRateSource(
    IGyroscope gyroscope,
    DeviceOrientationReference orientation) : IHeadingRateProvider
{
    // tune processNoise against real turn data before trusting low values
    private readonly KalmanFilter1D _filter = new(initialEstimate: 0, errorMeasure: 0.5, processNoise: 0.05);

    // gyro bias dominates DR error: 0.5 deg/s uncorrected is ~30 degrees of
    // heading error after a minute
    private const double DeadbandDegPerSec = 0.15;

    private EventHandler<double>? _headingRateChanged;

    event EventHandler<double> IHeadingRateProvider.HeadingRateChanged
    {
        add => _headingRateChanged += value;
        remove => _headingRateChanged -= value;
    }

    void IDeadReckoningSensorInput.Start()
    {
        gyroscope.ReadingChanged += OnReadingChanged;
        gyroscope.Start(SensorSpeed.Game);
    }

    void IDeadReckoningSensorInput.Stop()
    {
        gyroscope.ReadingChanged -= OnReadingChanged;
        gyroscope.Stop();
    }

    private void OnReadingChanged(object? sender, GyroscopeChangedEventArgs e)
    {
        var yawRadPerSec = orientation.YawRateAbout(e.Reading.AngularVelocity);
        var rawDegPerSec = yawRadPerSec * (180.0 / Math.PI);

        if (Math.Abs(rawDegPerSec) < DeadbandDegPerSec)
            rawDegPerSec = 0;

        _headingRateChanged?.Invoke(this, _filter.Update(rawDegPerSec));
    }
}
