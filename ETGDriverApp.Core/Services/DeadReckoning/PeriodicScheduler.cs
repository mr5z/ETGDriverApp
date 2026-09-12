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
        lock (_sync)
        {
            if (_running)
                return;

            _running = true;
            _interval = interval;
            _timer = new PeriodicTimer(interval);
            _cts = new CancellationTokenSource();

            var token = _cts.Token;
            var timer = _timer;

            // Assigned inside the lock. It used to be set after the lock was
            // released, which left a window where StopAsync could capture a
            // null _loop, skip the await and return while the tick callback
            // was still running - so a caller that stopped and immediately
            // disposed its collaborators could be called back afterwards.
            _loop = Task.Run(() => RunAsync(timer, onTick, token), token);
        }
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
        PeriodicTimer? timer;

        lock (_sync)
        {
            if (!_running)
                return;

            cts = _cts;
            loop = _loop;
            timer = _timer;
            _cts = null;
            _loop = null;
            _timer = null;
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

        // disposed only once the loop has observed cancellation and stopped
        // awaiting it
        timer?.Dispose();
    }

    // The timer is passed in rather than re-read from the field: a Start
    // racing this loop must not be able to swap the timer out from under it.
    private static async Task RunAsync(
        PeriodicTimer timer, Func<CancellationToken, Task> onTick, CancellationToken ct)
    {
        try
        {
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
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}