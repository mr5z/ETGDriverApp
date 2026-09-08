namespace ETGDriverApp.Services;

// Gravity on -Z plus vibration whose variance straddles the thresholds in
// AccelerometerMotionStateSource, so parking at B actually freezes DR.
internal class SimulatedAccelerometer(SimulatedVehicleState vehicle) : IAccelerometer
{
    private const double MovingNoiseG = 0.45;
    private const double StationaryNoiseG = 0.02;

    private static readonly TimeSpan GameInterval = TimeSpan.FromMilliseconds(20);

    private readonly Random _random = new();

    private CancellationTokenSource? _cts;

    public event EventHandler<AccelerometerChangedEventArgs>? ReadingChanged;

    public event EventHandler? ShakeDetected;

    public bool IsSupported => true;

    public bool IsMonitoring { get; private set; }

    public void Start(SensorSpeed sensorSpeed)
    {
        if (IsMonitoring)
            return;

        IsMonitoring = true;
        _cts = new CancellationTokenSource();

        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        if (!IsMonitoring)
            return;

        IsMonitoring = false;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(GameInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var noise = vehicle.IsMoving ? MovingNoiseG : StationaryNoiseG;

                ReadingChanged?.Invoke(this, new AccelerometerChangedEventArgs(
                    new AccelerometerData(
                        Jitter(0, noise),
                        Jitter(0, noise),
                        Jitter(-1, noise))));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private float Jitter(double centre, double amplitude) =>
        (float)(centre + (_random.NextDouble() - 0.5) * 2 * amplitude);
}