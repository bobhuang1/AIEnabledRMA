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
    /// Customer lookups one caller may make in any rolling 24 hours. Zero disables the limit.
    /// </summary>
    /// <remarks>
    /// Not "per session": a session is a process, and a process is not what the cap is about. The
    /// transport may run more than one replica, and a cap that reset whenever a caller was routed
    /// to a fresh replica would bound nothing. A day is long enough that ordinary work never feels
    /// it and short enough that a walk of the customer table costs a fresh day.
    /// </remarks>
    public int CustomerLookupsPerDay { get; init; } = 200;
}

/// <summary>
/// The counter behind <see cref="CallBudget"/>, deliberately an interface so the storage can move
/// between the process and the database without the tools knowing.
/// </summary>
public interface ICallMeter
{
    /// <summary>
    /// Takes one place in the caller's window, or reports that it is full.
    /// </summary>
    /// <remarks>
    /// The decision and the record of it are one operation on purpose. A caller that asks, is told
    /// yes, and is counted afterwards lets concurrent calls through the same gap; a meter that only
    /// counts would need the caller to be refused after the work was done.
    /// </remarks>
    Task<bool> TryTakeAsync(
        string caller,
        string kind,
        int limit,
        DateTimeOffset now,
        TimeSpan window,
        CancellationToken cancellationToken);
}

/// <summary>The answer a budget check gives.</summary>
/// <param name="Allowed">True when the caller may proceed, and its place has been counted.</param>
/// <param name="Refusal">What to tell the caller when it may not, phrased for a model reading it.</param>
public sealed record BudgetDecision(bool Allowed, string? Refusal)
{
    public static BudgetDecision Yes { get; } = new(true, null);

    public static BudgetDecision No(string refusal) => new(false, refusal);
}

/// <summary>
/// Counts the calls one caller is allowed to make, on either transport.
/// </summary>
/// <remarks>
/// The boundary the budget cares about is the agent walking the data, and that is not the same
/// thing as the process. On stdio it is: one agent launches one process. On HTTP a single process
/// serves every client, so a budget held per process would be one shared allowance that any caller
/// could exhaust for the others; and a budget held per process also multiplies by the number of
/// replicas, which is why the counter itself lives in the database (see
/// <see cref="PostgresCallMeter"/>) rather than in a field here.
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
    ICallMeter meter,
    Microsoft.Extensions.Options.IOptions<McpBudgetOptions> options,
    TimeProvider clock,
    ICallerIdentity caller)
{
    /// <summary>The stamp kind for any tool call.</summary>
    private const string CallKind = "call";

    /// <summary>The stamp kind for a customer lookup.</summary>
    private const string LookupKind = "lookup";

    private static readonly TimeSpan CallWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LookupWindow = TimeSpan.FromDays(1);

    private readonly McpBudgetOptions _options = options.Value;

    /// <summary>Counts one call of any tool against this caller's rolling minute.</summary>
    public async Task<BudgetDecision> TryConsumeCallAsync(CancellationToken cancellationToken = default)
    {
        if (_options.CallsPerMinute <= 0)
        {
            return BudgetDecision.Yes;
        }

        var taken = await meter.TryTakeAsync(
            caller.Describe(),
            CallKind,
            _options.CallsPerMinute,
            clock.GetUtcNow(),
            CallWindow,
            cancellationToken);

        return taken
            ? BudgetDecision.Yes
            : BudgetDecision.No(
                $"This caller has made {_options.CallsPerMinute} tool calls in the last minute, "
                + "which is the configured limit. Wait a minute and try again.");
    }

    /// <summary>
    /// Counts one customer lookup against this caller's daily cap. The rolling minute is not
    /// touched here: that is the guard's job, and counting it twice would halve the configured rate.
    /// </summary>
    public async Task<BudgetDecision> TryConsumeCustomerLookupAsync(
        CancellationToken cancellationToken = default)
    {
        if (_options.CustomerLookupsPerDay <= 0)
        {
            return BudgetDecision.Yes;
        }

        var taken = await meter.TryTakeAsync(
            caller.Describe(),
            LookupKind,
            _options.CustomerLookupsPerDay,
            clock.GetUtcNow(),
            LookupWindow,
            cancellationToken);

        return taken
            ? BudgetDecision.Yes
            : BudgetDecision.No(
                $"This caller has reached its limit of {_options.CustomerLookupsPerDay} customer "
                + "lookups in the last 24 hours. Ask an operator for the record instead.");
    }
}
