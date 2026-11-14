namespace AIEnabledRma.Mcp;

/// <summary>Per-session limits for agent calls. Bound from <c>Mcp:Budget</c>.</summary>
public sealed class McpBudgetOptions
{
    public const string SectionName = "Mcp:Budget";

    /// <summary>Audited tool calls allowed in any rolling minute. Zero disables the limit.</summary>
    public int CallsPerMinute { get; init; } = 60;

    /// <summary>Customer lookups allowed for the life of the process. Zero disables the limit.</summary>
    public int CustomerLookupsPerSession { get; init; } = 200;
}

/// <summary>
/// Counts the calls one agent session is allowed to make.
/// </summary>
/// <remarks>
/// A stdio server is one session per launched process, which is the boundary the budget cares
/// about: an agent that walks the customer table does it inside one process. The per-minute limit
/// stops a burst; the per-session limit stops a slow walk that never bursts. Both are counted
/// before the work is done, so a refused call costs nothing.
/// </remarks>
public sealed class CallBudget(Microsoft.Extensions.Options.IOptions<McpBudgetOptions> options, TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly McpBudgetOptions _options = options.Value;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _recent = new();
    private int _customerLookups;

    public bool TryConsumeCustomerLookup(out string? refusal)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();

            if (_options.CallsPerMinute > 0)
            {
                while (_recent.Count > 0 && now - _recent.Peek() >= Window)
                {
                    _recent.Dequeue();
                }

                if (_recent.Count >= _options.CallsPerMinute)
                {
                    refusal = $"This session has made {_options.CallsPerMinute} tool calls in the last "
                              + "minute, which is the configured limit. Wait a minute and try again.";
                    return false;
                }
            }

            if (_options.CustomerLookupsPerSession > 0
                && _customerLookups >= _options.CustomerLookupsPerSession)
            {
                refusal = $"This session has reached its limit of {_options.CustomerLookupsPerSession} "
                          + "customer lookups. Start a new session, or ask an operator for the record.";
                return false;
            }

            _recent.Enqueue(now);
            _customerLookups++;
            refusal = null;
            return true;
        }
    }
}
