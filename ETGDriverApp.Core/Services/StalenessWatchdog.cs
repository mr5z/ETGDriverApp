using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Models;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Services;

internal interface IStalenessWatchdog
{
    void Start();

    Task StopAsync();
}

internal class StalenessWatchdog(
    IOptionsMonitor<PositioningOptions> options,
    IPositionFilterPipeline pipeline,
    IPositionStateMachine stateMachine,
    PositionFeed feed,
    TimeProvider clock) : IStalenessWatchdog, IAsyncDisposable
{
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
        // read once: a change to the tick rate takes effect on the next
        // session. The thresholds it compares against are still read per
        // tick, which is where a live change actually matters.
        using var timer = new PeriodicTimer(options.CurrentValue.Staleness.TickInterval);

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
        var staleness = options.CurrentValue.Staleness;
        var now = clock.GetUtcNow();

        TimeSpan sinceFix;
        bool forcedFixDue;

        lock (_sync)
        {
            sinceFix = now - _lastRealFixAt;
            forcedFixDue = now - _lastForcedFixAt >= staleness.MinTimeBetweenForcedFixes;
        }

        // repeated calls are intended: the filter's uncertainty grows between
        // them, so the give-up threshold is reached on a later tick
        if (sinceFix >= staleness.HardThreshold)
            stateMachine.NotifyFixStale(pipeline.PredictUncertaintyMeters(now));

        if (sinceFix >= staleness.SoftThreshold && forcedFixDue)
        {
            lock (_sync)
                _lastForcedFixAt = now;

            await feed.ForcedFixAsync(ct);
        }
    }
}
