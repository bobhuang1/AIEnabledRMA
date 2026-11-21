using AIEnabledRma.Data;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// The budget when the transport runs as more than one process.
/// </summary>
/// <remarks>
/// Two service providers, each with its own <see cref="RmaDbContext"/> and its own
/// <see cref="PostgresCallMeter"/>, are as close to two replicas as a test can get without two
/// containers: nothing is shared between them except the database. That is the point — the earlier
/// counter was a dictionary in the process, so two replicas each handed out the full allowance and
/// each honestly reported itself as within the limit. These tests fail if the counter returns to
/// process memory, and they run against real PostgreSQL rather than a stub because the atomicity
/// being tested (the advisory lock and the rollback) is the database's.
/// </remarks>
public sealed class McpSharedBudgetTests
{
    private static ServiceProvider Replica(string connectionString)
    {
        var services = new ServiceCollection();

        services.AddDbContext<RmaDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        return services.BuildServiceProvider();
    }

    private static CallBudget Budget(ServiceProvider replica, string who, McpBudgetOptions options) =>
        new(
            new PostgresCallMeter(replica.GetRequiredService<IServiceScopeFactory>()),
            Options.Create(options),
            TimeProvider.System,
            new FixedCaller(who));

    private static async Task<LiveDatabase> StartedAsync(string prefix)
    {
        var database = new LiveDatabase(prefix);
        await database.InitializeAsync();

        await using var replica = Replica(database.ConnectionString);
        await using var scope = replica.CreateAsyncScope();
        await SchemaBootstrapper.InitializeAsync(
            scope.ServiceProvider.GetRequiredService<RmaDbContext>(),
            cancellationToken: TestContext.Current.CancellationToken);

        return database;
    }

    [Fact]
    public async Task The_minute_window_is_spent_once_across_two_replicas()
    {
        var options = new McpBudgetOptions { CallsPerMinute = 2, CustomerLookupsPerDay = 100 };

        await using var database = await StartedAsync("mcpbudget_calls");
        await using var first = Replica(database.ConnectionString);
        await using var second = Replica(database.ConnectionString);

        var onFirst = Budget(first, "rma-agent:agent-1", options);
        var onSecond = Budget(second, "rma-agent:agent-1", options);

        // The allowance is two, spent one call per replica: the third call is refused whichever
        // replica answers it.
        Assert.True((await onFirst.TryConsumeCallAsync(TestContext.Current.CancellationToken)).Allowed);
        Assert.True((await onSecond.TryConsumeCallAsync(TestContext.Current.CancellationToken)).Allowed);

        var refused = await onFirst.TryConsumeCallAsync(TestContext.Current.CancellationToken);

        Assert.False(refused.Allowed);
        Assert.NotNull(refused.Refusal);
        Assert.Contains("limit", refused.Refusal, StringComparison.OrdinalIgnoreCase);

        // And the other replica agrees, which is what a per-process counter could not do.
        Assert.False((await onSecond.TryConsumeCallAsync(TestContext.Current.CancellationToken)).Allowed);
    }

    [Fact]
    public async Task A_different_caller_keeps_its_own_window_on_another_replica()
    {
        var options = new McpBudgetOptions { CallsPerMinute = 1, CustomerLookupsPerDay = 100 };

        await using var database = await StartedAsync("mcpbudget_split");
        await using var first = Replica(database.ConnectionString);
        await using var second = Replica(database.ConnectionString);

        var agentOne = Budget(first, "rma-agent:agent-1", options);
        var agentTwo = Budget(second, "rma-agent:agent-2", options);

        Assert.True((await agentOne.TryConsumeCallAsync(TestContext.Current.CancellationToken)).Allowed);
        Assert.False((await agentOne.TryConsumeCallAsync(TestContext.Current.CancellationToken)).Allowed);

        // A shared allowance would refuse this caller too; a shared counter must not be a shared
        // quota.
        Assert.True((await agentTwo.TryConsumeCallAsync(TestContext.Current.CancellationToken)).Allowed);
    }

    [Fact]
    public async Task The_customer_lookup_cap_is_shared_across_replicas()
    {
        // One call per minute: if the lookup counter touched the minute window, the call below
        // would be refused and the test would say so.
        var options = new McpBudgetOptions { CallsPerMinute = 1, CustomerLookupsPerDay = 1 };

        await using var database = await StartedAsync("mcpbudget_lookups");
        await using var first = Replica(database.ConnectionString);
        await using var second = Replica(database.ConnectionString);

        var onFirst = Budget(first, "rma-agent:agent-1", options);
        var onSecond = Budget(second, "rma-agent:agent-1", options);

        Assert.True((await onFirst.TryConsumeCustomerLookupAsync(TestContext.Current.CancellationToken)).Allowed);

        var refused = await onSecond.TryConsumeCustomerLookupAsync(TestContext.Current.CancellationToken);

        Assert.False(refused.Allowed);
        Assert.NotNull(refused.Refusal);
        Assert.Contains("customer lookups", refused.Refusal, StringComparison.OrdinalIgnoreCase);

        // The minute window was untouched by the lookup counter: a lookup is not a second call.
        Assert.True((await onSecond.TryConsumeCallAsync(TestContext.Current.CancellationToken)).Allowed);
        Assert.False((await onFirst.TryConsumeCallAsync(TestContext.Current.CancellationToken)).Allowed);
    }

    private sealed class FixedCaller(string name) : ICallerIdentity
    {
        public string Describe() => name;
    }
}
