namespace ETGDriverApp.Services;

// Emits yaw about the device Z axis, which DeviceOrientationReference reads
// via the gravity vector. A deliberate bias reproduces the drift DR actually
// suffers from and gives RecalibrateAgainst something to correct.
internal class SimulatedGyroscope(SimulatedVehicleState vehicle) : IGyroscope
{
    private const double BiasDegPerSec = 0.4;

    private static readonly TimeSpan GameInterval = TimeSpan.FromMilliseconds(20);

    private readonly Random _random = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event EventHandler<GyroscopeChangedEventArgs>? ReadingChanged;

    public bool IsSupported => true;

    public bool IsMonitoring { get; private set; }

    public void Start(SensorSpeed sensorSpeed)
    {
        if (IsMonitoring)
            return;

        IsMonitoring = true;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        if (!IsMonitoring)
            return;

        IsMonitoring = false;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(GameInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var degPerSec = vehicle.HeadingRateDegPerSec
                                + BiasDegPerSec
                                + (_random.NextDouble() - 0.5) * 0.2;

                // gravity rests near -Z, so YawRateAbout returns -Z; negate
                // so a positive turn rate reads as a positive heading rate
                var radPerSec = (float)(-degPerSec * Math.PI / 180.0);

                ReadingChanged?.Invoke(this, new GyroscopeChangedEventArgs(
                    new GyroscopeData(0, 0, radPerSec)));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}