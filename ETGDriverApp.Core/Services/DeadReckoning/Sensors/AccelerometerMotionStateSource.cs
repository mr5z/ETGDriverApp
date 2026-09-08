using Microsoft.Maui.Devices.Sensors;

namespace ETGDriverApp.Core.Services.DeadReckoning.Sensors;

internal class AccelerometerMotionStateSource(
    IAccelerometer accelerometer,
    DeviceOrientationReference orientation) : IMotionStateProvider
{
    // gravity contributes ~1g to the magnitude regardless of orientation and
    // cancels out; what remains is vehicle vibration
    private const double StationaryVarianceThreshold = 0.02;
    private const double MovingVarianceThreshold = 0.05;
    private const int WindowSize = 20;

    // a vehicle at a red light must not flap the DR freeze on and off
    private const int RequiredConsecutive = 5;

    private readonly Queue<double> _recentMagnitudes = new();

    private MotionState _published = MotionState.Unknown;
    private MotionState _candidate = MotionState.Unknown;
    private int _candidateCount;

    private EventHandler<MotionState>? _motionStateChanged;

    event EventHandler<MotionState> IMotionStateProvider.MotionStateChanged
    {
        add => _motionStateChanged += value;
        remove => _motionStateChanged -= value;
    }

    void IDeadReckoningSensorInput.Start()
    {
        accelerometer.ReadingChanged += OnReadingChanged;
        accelerometer.Start(SensorSpeed.Game);
    }

    void IDeadReckoningSensorInput.Stop()
    {
        accelerometer.ReadingChanged -= OnReadingChanged;
        accelerometer.Stop();
    }

    private void OnReadingChanged(object? sender, AccelerometerChangedEventArgs e)
    {
        var a = e.Reading.Acceleration;

        orientation.UpdateFromAcceleration(a);

        var magnitude = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);

        _recentMagnitudes.Enqueue(magnitude);

        if (_recentMagnitudes.Count > WindowSize)
            _recentMagnitudes.Dequeue();

        if (_recentMagnitudes.Count < WindowSize)
            return;

        var mean = _recentMagnitudes.Average();
        var variance = _recentMagnitudes.Average(m => (m - mean) * (m - mean));

        var observed = variance switch
        {
            < StationaryVarianceThreshold => MotionState.Stationary,
            > MovingVarianceThreshold => MotionState.Moving,
            _ => _published
        };

        if (observed != _candidate)
        {
            _candidate = observed;
            _candidateCount = 0;
        }

        if (++_candidateCount < RequiredConsecutive)
            return;

        if (observed == _published)
            return;

        _published = observed;
        _motionStateChanged?.Invoke(this, observed);
    }
}
