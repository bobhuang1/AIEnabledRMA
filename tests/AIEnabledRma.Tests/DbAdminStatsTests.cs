using AIEnabledRma.Domain.Operations;
using AIEnabledRma.Tools.DbAdmin;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Tests;

/// <summary>
/// `DbAdmin stats` is the only view an operator has of how fast the four audit tables fill between
/// prune runs, so its output is pinned here rather than left to whatever the command happens to
/// print: a table that drops out of the list reads exactly like a table that never grows.
/// </summary>
public sealed class DbAdminStatsTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _database = new("rma_stats");

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();

        await using var db = _database.CreateContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public void The_report_names_every_table_and_keeps_the_two_groups_apart()
    {
        var lines = StatsReport.Format(
        [
            new TableRowCount("products", 5),
            new TableRowCount("devices", 14),
            new TableRowCount("warranties", 16),
            new TableRowCount("customers", 8),
            new TableRowCount("addresses", 10),
            new TableRowCount("rma_requests", 1),
            new TableRowCount("rma_lines", 1),
            new TableRowCount("mcp_tool_calls", 240),
            new TableRowCount("operator_actions", 12),
            new TableRowCount("review_items", 3),
            new TableRowCount("webhook_events", 900),
        ]);

        Assert.Equal(
        [
            "  products:      5",
            "  devices:       14",
            "  warranties:    16",
            "  customers:     8",
            "  addresses:     10",
            "  rma_requests:  1",
            "  rma_lines:     1",
            string.Empty,
            "  mcp_tool_calls:   240",
            "  operator_actions: 12",
            "  review_items:     3",
            "  webhook_events:   900",
        ],
            lines);
    }

    [Fact]
    public async Task The_audit_counts_are_read_from_the_database_and_not_guessed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var seed = _database.CreateContext())
        {
            // Two of the four are left empty on purpose: a report that printed a constant, or that
            // answered from the wrong table, would still say something for each of them.
            seed.WebhookEvents.AddRange(
                new ProcessedWebhookEvent
                {
                    EventId = "evt_one",
                    Type = "payment_intent.succeeded",
                    ReceivedAtUtc = Now,
                },
                new ProcessedWebhookEvent
                {
                    EventId = "evt_two",
                    Type = "payment_intent.succeeded",
                    ReceivedAtUtc = Now,
                });

            seed.ReviewItems.Add(new ReviewItem
            {
                Id = Guid.NewGuid(),
                CreatedAtUtc = Now,
                Source = ReviewSources.PolicyHumanReview,
                Reason = "counted by stats",
                Status = ReviewStatuses.Open,
            });

            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var db = _database.CreateContext();
        var counts = (await StatsReport.ReadAsync(db, cancellationToken))
            .ToDictionary(count => count.Table, count => count.Rows);

        Assert.Equal(11, counts.Count);
        Assert.Equal(2, counts["webhook_events"]);
        Assert.Equal(1, counts["review_items"]);
        Assert.Equal(0, counts["mcp_tool_calls"]);
        Assert.Equal(0, counts["operator_actions"]);
    }
}
