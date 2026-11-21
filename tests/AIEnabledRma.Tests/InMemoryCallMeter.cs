using AIEnabledRma.Mcp;

namespace AIEnabledRma.Tests;

/// <summary>
/// The rolling-window meter, in memory, for the unit tests that are about the budget's rules rather
/// than about where the counter lives.
/// </summary>
/// <remarks>
/// Production uses <see cref="PostgresCallMeter"/>: a meter per process cannot bound a transport
/// that runs more than one replica. What is tested here is the window arithmetic and the per-caller
/// split, which are the same in both, and the shared behaviour across replicas is tested against a
/// real PostgreSQL in <see cref="McpSharedBudgetTests"/>.
/// </remarks>
internal sealed class InMemoryCallMeter : ICallMeter
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Caller, string Kind), Queue<DateTimeOffset>> _stamps = [];

    public Task<bool> TryTakeAsync(
        string caller,
        string kind,
        int limit,
        DateTimeOffset now,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_stamps.TryGetValue((caller, kind), out var stamps))
            {
                stamps = new Queue<DateTimeOffset>();
                _stamps[(caller, kind)] = stamps;
            }

            while (stamps.Count > 0 && now - stamps.Peek() >= window)
            {
                stamps.Dequeue();
            }

            if (stamps.Count >= limit)
            {
                return Task.FromResult(false);
            }

            stamps.Enqueue(now);
            return Task.FromResult(true);
        }
    }
}
