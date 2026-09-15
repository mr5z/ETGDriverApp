using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Diagnostics;
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
    TimeProvider clock,
    IPositioningDiagnostics diagnostics) : IStalenessWatchdog, IAsyncDisposable
{
    private const string TraceCategory = "staleness";
    
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _sync = new();

    private DateTimeOffset _lastRealFixAt;
    private DateTimeOffset _lastForcedFixAt = DateTimeOffset.MinValue;
    private double _lastTracedUncertainty;
    private Task? _loop;

    void IStalenessWatchdog.Start()
    {
        if (_loop is not null)
            return;

        _lastRealFixAt = clock.GetUtcNow();
        _lastTracedUncertainty = 0;
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

        var now = clock.GetUtcNow();

        // When the world was last actually observed, which is the fix's own
        // timestamp rather than the moment we processed it: a background wake
        // delivers a batch of minutes-old fixes at once, and treating those as
        // fresh would silence the watchdog exactly when it should be firing.
        // PositionStateMachine.RecordObservation does the same, and the two must
        // agree about how stale the track is.
        var observedAt = position.Timestamp > now ? now : position.Timestamp;

        lock (_sync)
        {
            // within a stale batch, fixes can arrive out of order; an older one
            // must not drag the clock back behind a newer one already recorded
            if (_lastRealFixAt > observedAt)
                return;

            _lastRealFixAt = observedAt;
        }
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

        // Predict from the SOFT threshold, not the hard one. The prediction
        // is what ages the published radius (IPositionFilterPipeline.Current),
        // and between the two thresholds the last fix is deliberately held -
        // it should be held honestly. Nothing else changes before the hard
        // threshold: the state machine is still only told at that point.
        if (sinceFix < staleness.SoftThreshold)
            return;

        // repeated calls are intended: the filter's uncertainty grows between
        // them, so the give-up threshold is reached on a later tick
        var uncertainty = pipeline.PredictUncertaintyMeters(now);

        if (sinceFix >= staleness.HardThreshold)
        {
            // Covariance may only shrink on evidence, and while blind there
            // is none - so a fall here is a contradiction worth naming. It
            // caught the DR feedback loop: uncertainty dropping 141m -> 29m
            // in four seconds on samples derived from the filter's own past
            // output. A zero-velocity update can legitimately tighten things
            // a little, since a detected stop is real evidence, but it is
            // evidence about velocity and should not collapse position.
            if (_lastTracedUncertainty > 0 && uncertainty < _lastTracedUncertainty * 0.8)
            {
                diagnostics.Trace(TraceCategory,
                    $"uncertainty FELL while blind: {_lastTracedUncertainty:F0}m -> " +
                    $"{uncertainty:F0}m since={sinceFix.TotalSeconds:F1}s");
            }

            _lastTracedUncertainty = uncertainty;

            // The number the state machine gives up on is the filter's
            // covariance. Published radius derives from it for DR samples,
            // and now for a held fix too, so the two agreeing is expected;
            // what is worth watching is the SOURCE, since a real fix landing
            // here means the watchdog and the pipeline disagree about
            // staleness. `claimed` stays the producer's own figure by design.
            if (diagnostics.IsEnabled)
            {
                var published = pipeline.Current;

                diagnostics.Trace(TraceCategory,
                    $"stale since={sinceFix.TotalSeconds:F1}s " +
                    $"filter={uncertainty:F0}m " +
                    $"published={published?.EffectiveRadiusMeters:F0}m " +
                    $"claimed={published?.AccuracyMeters:F0}m " +
                    $"source={published?.SourceType}");
            }

            stateMachine.NotifyFixStale(uncertainty);
        }

        if (forcedFixDue)
        {
            lock (_sync)
                _lastForcedFixAt = now;

            if (diagnostics.IsEnabled)
                diagnostics.Trace(TraceCategory, $"forcing fix since={sinceFix.TotalSeconds:F1}s");

            await feed.ForcedFixAsync(ct);
        }
    }
}