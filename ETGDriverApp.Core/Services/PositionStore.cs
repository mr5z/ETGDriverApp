using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Models;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core.Services;

public interface IPositionStore
{
    Task PersistAsync(NormalizedPosition position, CancellationToken ct = default);

    Task<IReadOnlyList<NormalizedPosition>> GetSinceAsync(
        DateTimeOffset since, CancellationToken ct = default);

    Task<NormalizedPosition?> GetLatestAsync(CancellationToken ct = default);

    Task PruneBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

// Does NOT survive process death, which is the store's actual purpose.
// Placeholder for wiring and tests; back it with durable storage.
internal class InMemoryPositionStore(IOptionsMonitor<PositioningOptions> options) : IPositionStore
{
    private readonly List<NormalizedPosition> _positions = [];
    private readonly Lock _sync = new();

    private int _writesSincePrune;

    Task IPositionStore.PersistAsync(NormalizedPosition position, CancellationToken ct)
    {
        var store = options.CurrentValue.Store;

        lock (_sync)
        {
            _positions.Add(position);

            if (++_writesSincePrune >= store.PruneEveryWrites)
            {
                _writesSincePrune = 0;
                _positions.RemoveAll(p => p.Timestamp < position.Timestamp - store.Retention);
            }
        }

        return Task.CompletedTask;
    }

    Task<IReadOnlyList<NormalizedPosition>> IPositionStore.GetSinceAsync(
        DateTimeOffset since, CancellationToken ct)
    {
        lock (_sync)
        {
            IReadOnlyList<NormalizedPosition> result =
            [
                .. _positions.Where(p => p.Timestamp >= since).OrderBy(p => p.Timestamp)
            ];

            return Task.FromResult(result);
        }
    }

    Task<NormalizedPosition?> IPositionStore.GetLatestAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_positions.Count == 0)
                return Task.FromResult<NormalizedPosition?>(null);

            return Task.FromResult<NormalizedPosition?>(_positions.MaxBy(p => p.Timestamp));
        }
    }

    Task IPositionStore.PruneBeforeAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        lock (_sync)
            _positions.RemoveAll(p => p.Timestamp < cutoff);

        return Task.CompletedTask;
    }
}
