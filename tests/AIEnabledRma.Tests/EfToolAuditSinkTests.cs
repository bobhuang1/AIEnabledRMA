using AIEnabledRma.Data;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Mcp;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Tests;

/// <summary>
/// The real audit sink and its migration, against PostgreSQL.
/// </summary>
/// <remarks>
/// Every other audit assertion is made against <see cref="FakeToolAuditSink"/>, which cannot fail
/// the way the real one can: the row is written by EF into a table that only the
/// <c>AddMcpToolCalls</c> migration creates. A column renamed in the model but not in the
/// migration, an unapplied migration, or a value the database rejects would leave the trail
/// silently empty while all the fake-backed tests stayed green. So this suite migrates a fresh
/// database, writes through the sink the host registers, and reads the row back with a different
/// context.
/// </remarks>
public sealed class EfToolAuditSinkTests : IAsyncLifetime
{
    private readonly LiveDatabase _database = new("rma_audit_test");

    public async ValueTask InitializeAsync() => await _database.InitializeAsync();

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task A_recorded_call_reaches_the_migrated_table_and_reads_back_intact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await MigrateAsync(cancellationToken);

        var at = new DateTimeOffset(2026, 11, 14, 9, 30, 0, TimeSpan.Zero);
        var record = new ToolCallRecord
        {
            Tool = "find_device",
            Caller = ToolCallGuard.CallerName,
            ArgumentHashes = ["identifier:0123456789abcdef", "take:fedcba9876543210"],
            Reason = "customer called to correct the phone number on their return",
            ResultCount = 3,
            LatencyMs = 42,
            Refused = false,
            AtUtc = at,
        };

        await using (var write = _database.CreateContext())
        {
            await new EfToolAuditSink(write).RecordAsync(record, cancellationToken);
        }

        // A second context, so the assertion is about what the server stores rather than what the
        // change tracker still remembers.
        await using var read = _database.CreateContext();
        var row = Assert.Single(await read.McpToolCalls.AsNoTracking().ToListAsync(cancellationToken));

        Assert.Equal("find_device", row.Tool);
        Assert.Equal("stdio:local", row.Caller);
        Assert.Equal("identifier:0123456789abcdef,take:fedcba9876543210", row.ArgumentHashes);

        // The reason is the one argument value the trail keeps, and it has to survive the column:
        // an unmask recorded without it is a record of the act and not of its justification.
        Assert.Equal("customer called to correct the phone number on their return", row.Reason);
        Assert.Equal(3, row.ResultCount);
        Assert.Equal(42, row.LatencyMs);
        Assert.False(row.Refused);
        Assert.Equal(at, row.AtUtc);
    }

    [Fact]
    public async Task A_refused_call_with_no_arguments_is_stored_as_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await MigrateAsync(cancellationToken);

        await using (var write = _database.CreateContext())
        {
            await new EfToolAuditSink(write).RecordAsync(
                new ToolCallRecord
                {
                    Tool = "check_warranty_coverage",
                    Caller = ToolCallGuard.CallerName,
                    ArgumentHashes = [],
                    ResultCount = 0,
                    LatencyMs = 1,
                    Refused = true,
                    AtUtc = DateTimeOffset.UtcNow,
                },
                cancellationToken);
        }

        await using var read = _database.CreateContext();
        var row = Assert.Single(await read.McpToolCalls.AsNoTracking().ToListAsync(cancellationToken));

        // A refused call has no arguments to hash, and the column is required: the sink writes an
        // empty string rather than failing the call it was trying to record.
        Assert.True(row.Refused);
        Assert.Equal(string.Empty, row.ArgumentHashes);
        Assert.Null(row.Reason);
        Assert.Equal(0, row.ResultCount);
    }

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var migration = _database.CreateContext();
        await migration.Database.MigrateAsync(cancellationToken);
    }
}
