namespace AIEnabledRma.Mcp;

/// <summary>Per-session limits for agent calls. Bound from <c>Mcp:Budget</c>.</summary>
public sealed class McpBudgetOptions
{
    public const string SectionName = "Mcp:Budget";

    /// <summary>
    /// Calls of any tool allowed in any rolling minute. Zero disables the limit. Counted at
    /// <see cref="ToolCallGuard"/>, so it covers every tool rather than the customer ones only.
    /// </summary>
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
///
/// The minute window is consumed once per tool call, by the guard every tool passes through. The
/// customer-lookup cap is consumed inside the customer tools, because it is about what a call is
/// for, not how many calls there are: walking the device catalog is cheap, walking the customer
/// table is not.
/// </remarks>
public sealed class CallBudget(Microsoft.Extensions.Options.IOptions<McpBudgetOptions> options, TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly McpBudgetOptions _options = options.Value;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _recent = new();
    private int _customerLookups;

    /// <summary>Counts one call of any tool against the rolling minute.</summary>
    public bool TryConsumeCall(out string? refusal)
    {
        lock (_gate)
        {
            if (_options.CallsPerMinute > 0)
            {
                var now = clock.GetUtcNow();

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

                _recent.Enqueue(now);
            }

            refusal = null;
            return true;
        }
    }

    /// <summary>
    /// Counts one customer lookup against the session cap. The rolling minute is not touched
    /// here: that is the guard's job, and counting it twice would halve the configured rate.
    /// </summary>
    public bool TryConsumeCustomerLookup(out string? refusal)
    {
        lock (_gate)
        {
            if (_options.CustomerLookupsPerSession > 0
                && _customerLookups >= _options.CustomerLookupsPerSession)
            {
                refusal = $"This session has reached its limit of {_options.CustomerLookupsPerSession} "
                          + "customer lookups. Start a new session, or ask an operator for the record.";
                return false;
            }

            _customerLookups++;
            refusal = null;
            return true;
        }
    }
}
