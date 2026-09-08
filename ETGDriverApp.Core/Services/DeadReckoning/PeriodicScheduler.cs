namespace ETGDriverApp.Core.Services.DeadReckoning;

// PeriodicTimer rather than IDispatcher.CreateTimer: a dispatcher timer is a
// UI-thread timer and stops ticking while backgrounded on iOS
internal interface IPeriodicScheduler
{
    void Start(TimeSpan interval, Func<CancellationToken, Task> onTick);

    void SetInterval(TimeSpan interval);

    Task StopAsync();
}

internal class PeriodicScheduler : IPeriodicScheduler, IAsyncDisposable
{
    private readonly Lock _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private PeriodicTimer? _timer;
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private bool _running;

    void IPeriodicScheduler.Start(TimeSpan interval, Func<CancellationToken, Task> onTick)
    {
        CancellationToken token;

        lock (_sync)
        {
            if (_running)
                return;

            _running = true;
            _interval = interval;
            _timer = new PeriodicTimer(interval);
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }

        _loop = Task.Run(() => RunAsync(onTick, token));
    }

    void IPeriodicScheduler.SetInterval(TimeSpan interval)
    {
        lock (_sync)
        {
            _interval = interval;

            // takes effect from the next tick; no restart needed
            if (_timer is not null)
                _timer.Period = interval;
        }
    }

    async Task IPeriodicScheduler.StopAsync() => await StopCoreAsync();

    async ValueTask IAsyncDisposable.DisposeAsync() => await StopCoreAsync();

    private async Task StopCoreAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;

        lock (_sync)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
            _running = false;
        }

        if (cts is null)
            return;

        await cts.CancelAsync();

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();

        lock (_sync)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> onTick, CancellationToken ct)
    {
        PeriodicTimer? timer;

        lock (_sync)
            timer = _timer;

        if (timer is null)
            return;

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await onTick(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // one bad extrapolation must not stop the loop
            }
        }
    }
}
