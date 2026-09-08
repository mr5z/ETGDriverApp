using ETGDriverApp.Core.Models;

namespace ETGDriverApp.Core.Services;

internal interface IStalenessWatchdog
{
    void Start();

    Task StopAsync();
}

internal class StalenessWatchdog(
    IPositionFilterPipeline pipeline,
    IPositionStateMachine stateMachine,
    PositionFeed feed,
    TimeProvider clock) : IStalenessWatchdog, IAsyncDisposable
{
    private static readonly TimeSpan SoftThreshold = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan HardThreshold = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinTimeBetweenForcedFixes = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _sync = new();

    private DateTimeOffset _lastRealFixAt;
    private DateTimeOffset _lastForcedFixAt = DateTimeOffset.MinValue;
    private Task? _loop;

    void IStalenessWatchdog.Start()
    {
        if (_loop is not null)
            return;

        _lastRealFixAt = clock.GetUtcNow();
        pipeline.PositionUpdated += OnPositionUpdated;

        // PeriodicTimer, not IDispatcherTimer: the dispatcher does not tick
        // while backgrounded on iOS
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    async Task IStalenessWatchdog.StopAsync() => await StopCoreAsync();

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await StopCoreAsync();
        _cts.Dispose();
    }

    private async Task StopCoreAsync()
    {
        pipeline.PositionUpdated -= OnPositionUpdated;

        await _cts.CancelAsync();

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }

            _loop = null;
        }
    }

    private void OnPositionUpdated(object? sender, NormalizedPosition position)
    {
        // only a real fix resets the clock; a DR estimate is the symptom
        if (position.SourceType == PositionSourceType.DeadReckoned)
            return;

        lock (_sync)
            _lastRealFixAt = clock.GetUtcNow();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // a failed forced fix is itself a staleness signal
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        TimeSpan sinceFix;
        bool forcedFixDue;

        lock (_sync)
        {
            sinceFix = now - _lastRealFixAt;
            forcedFixDue = now - _lastForcedFixAt >= MinTimeBetweenForcedFixes;
        }

        // repeated calls are intended: each advances the uncertainty radius
        if (sinceFix >= HardThreshold)
            stateMachine.NotifyFixStale();

        if (sinceFix >= SoftThreshold && forcedFixDue)
        {
            lock (_sync)
                _lastForcedFixAt = now;

            await feed.ForcedFixAsync(ct);
        }
    }
}
