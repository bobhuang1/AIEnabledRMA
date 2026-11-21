using AIEnabledRma.Data;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Tests;

/// <summary>
/// The ledger the web host registers, against a real server: the property being claimed is that the
/// database's primary key decides which delivery is the first, and an in-memory double cannot show
/// that the unique constraint is there at all.
/// </summary>
public sealed class WebhookEventLedgerTests
{
    [Fact]
    public async Task The_same_delivery_is_recorded_once()
    {
        await using var database = new LiveDatabase("rma_webhook");
        await database.InitializeAsync();

        await using (var setup = database.CreateContext())
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = database.CreateContext())
        {
            var ledger = new EfWebhookEventLedger(db);
            var at = DateTimeOffset.UtcNow;

            Assert.True(await ledger.TryRecordAsync("evt_same", "payment_intent.canceled", at, TestContext.Current.CancellationToken));
            Assert.False(await ledger.TryRecordAsync("evt_same", "payment_intent.canceled", at, TestContext.Current.CancellationToken));

            // A failed insert must not poison the context: a delivery can arrive while the same
            // context still has work to do, and the next distinct event has to be recorded.
            Assert.True(await ledger.TryRecordAsync("evt_other", "payment_intent.payment_failed", at, TestContext.Current.CancellationToken));
        }

        await using (var check = database.CreateContext())
        {
            Assert.Equal(2, await check.WebhookEvents.CountAsync(TestContext.Current.CancellationToken));
        }
    }
}
