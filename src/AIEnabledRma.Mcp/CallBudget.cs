namespace AIEnabledRma.Mcp;

/// <summary>Per-caller limits for agent calls. Bound from <c>Mcp:Budget</c>.</summary>
public sealed class McpBudgetOptions
{
    public const string SectionName = "Mcp:Budget";

    /// <summary>
    /// Calls of any tool one caller may make in any rolling minute. Zero disables the limit.
    /// Counted at <see cref="ToolCallGuard"/>, so it covers every tool rather than the customer
    /// ones only.
    /// </summary>
    public int CallsPerMinute { get; init; } = 60;

    /// <summary>
    /// Customer lookups allowed to one caller for as long as it is tracked. Zero disables the
    /// limit. "One caller" is the process on stdio and the token's client and subject on HTTP.
    /// </summary>
    public int CustomerLookupsPerSession { get; init; } = 200;
}

/// <summary>
/// Counts the calls one caller is allowed to make.
/// </summary>
/// <remarks>
/// The boundary the budget cares about is the agent walking the data, and that is not the same
/// thing as the process. On stdio it is: one agent launches one process. On HTTP a single process
/// serves every client, so a budget held per process would be one shared allowance that any caller
/// could exhaust for the others — the first agent to run out would stop every agent behind it, and
/// the cap would say nothing about what one agent did.
///
/// So both counters are keyed on <see cref="ICallerIdentity.Describe"/>: the rolling minute and the
/// customer-lookup cap are per caller, and stdio still gets one caller per process. The per-minute
/// limit stops a burst; the lookup cap stops a slow walk that never bursts. Both are counted before
/// the work is done, so a refused call costs nothing.
///
/// The minute window is consumed once per tool call, by the guard every tool passes through. The
/// customer-lookup cap is consumed inside the customer tools, because it is about what a call is
/// for, not how many calls there are: walking the device catalog is cheap, walking the customer
/// table is not.
/// </remarks>
public sealed class CallBudget(
    Microsoft.Extensions.Options.IOptions<McpBudgetOptions> options,
    TimeProvider clock,
    ICallerIdentity caller)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many callers are tracked before the coldest is forgotten. The counters are held in
    /// memory, so an identity flood would otherwise grow the map until the process died.
    /// </summary>
    private const int MaxTrackedCallers = 1000;

    private readonly McpBudgetOptions _options = options.Value;
    private readonly object _gate = new();
    private readonly Dictionary<string, CallerState> _callers = new(StringComparer.Ordinal);

    private sealed class CallerState
    {
        public Queue<DateTimeOffset> Recent { get; } = new();

        public int CustomerLookups { get; set; }

        public DateTimeOffset LastSeen { get; set; }
    }

    /// <summary>Counts one call of any tool against this caller's rolling minute.</summary>
    public bool TryConsumeCall(out string? refusal)
    {
        lock (_gate)
        {
            refusal = null;
            if (_options.CallsPerMinute <= 0)
            {
                return true;
            }

            var now = clock.GetUtcNow();
            var state = StateFor(caller.Describe(), now);

            while (state.Recent.Count > 0 && now - state.Recent.Peek() >= Window)
            {
                state.Recent.Dequeue();
            }

            if (state.Recent.Count >= _options.CallsPerMinute)
            {
                refusal = $"This session has made {_options.CallsPerMinute} tool calls in the last "
                          + "minute, which is the configured limit. Wait a minute and try again.";
                return false;
            }

            state.Recent.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// Counts one customer lookup against this caller's cap. The rolling minute is not touched
    /// here: that is the guard's job, and counting it twice would halve the configured rate.
    /// </summary>
    public bool TryConsumeCustomerLookup(out string? refusal)
    {
        lock (_gate)
        {
            refusal = null;

            var state = StateFor(caller.Describe(), clock.GetUtcNow());

            if (_options.CustomerLookupsPerSession > 0
                && state.CustomerLookups >= _options.CustomerLookupsPerSession)
            {
                refusal = $"This session has reached its limit of {_options.CustomerLookupsPerSession} "
                          + "customer lookups. Start a new session, or ask an operator for the record.";
                return false;
            }

            state.CustomerLookups++;
            return true;
        }
    }

    private CallerState StateFor(string who, DateTimeOffset now)
    {
        if (!_callers.TryGetValue(who, out var state))
        {
            if (_callers.Count >= MaxTrackedCallers)
            {
                ForgetColdest();
            }

            state = new CallerState();
            _callers[who] = state;
        }

        state.LastSeen = now;
        return state;
    }

    /// <summary>
    /// Drops the caller that has been quiet longest, counters included.
    /// </summary>
    /// <remarks>
    /// Forgetting a caller gives it a fresh budget, which is a real trade: a thousand distinct
    /// callers alive at once is already past what this deployment serves, and the alternative is a
    /// map that a stream of tokens grows without bound. A per-caller budget that sometimes resets is
    /// better than an unbounded one that never does.
    /// </remarks>
    private void ForgetColdest()
    {
        var coldest = default(KeyValuePair<string, CallerState>);
        var found = false;

        foreach (var entry in _callers)
        {
            if (!found || entry.Value.LastSeen < coldest.Value.LastSeen)
            {
                coldest = entry;
                found = true;
            }
        }

        if (found)
        {
            _callers.Remove(coldest.Key);
        }
    }
}
