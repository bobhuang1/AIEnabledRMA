using AIEnabledRma.Data;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Operations;
using AIEnabledRma.Domain.Rma;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Tests;

/// <summary>
/// The ledger the web host registers, against a real server: the property being claimed is that the
/// database's primary key decides which delivery is the first, and an in-memory double cannot show
/// that the unique constraint is there at all. The second property is the one that matters more: a
/// delivery is never recorded as handled unless what it asked for was filed in the same transaction.
/// </summary>
public sealed class WebhookEventLedgerTests
{
    private static AIEnabledRma.Domain.Operations.ReviewItem Item(Guid id, DateTimeOffset at) => new()
    {
        Id = id,
        CreatedAtUtc = at,
        Source = ReviewSources.PaymentFailed,
        Reason = "Stripe:payment_intent.payment_failed",
        Explanation = "PaymentIntent pi_test for RMA-2026-00001.",
    };

    /// <summary>An item and the return its delivery names, which the ledger links before filing it.</summary>
    private static WebhookEffect Effect(Guid id, DateTimeOffset at, string? rmaNumber = null) =>
        new(Item(id, at), rmaNumber);

    [Fact]
    public async Task The_same_delivery_is_recorded_once()
    {
        await using var database = new LiveDatabase("rma_webhook");
        await database.InitializeAsync();

        await using (var setup = database.CreateContext())
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        var at = DateTimeOffset.UtcNow;

        await using (var db = database.CreateContext())
        {
            var ledger = new EfWebhookEventLedger(db);

            Assert.True(await ledger.TryAcceptAsync(
                new ProcessedWebhookEvent { EventId = "evt_same", Type = "payment_intent.canceled", ReceivedAtUtc = at },
                Effect(Guid.NewGuid(), at),
                TestContext.Current.CancellationToken));

            // The retry files nothing: the first delivery's item is the only one that should exist.
            Assert.False(await ledger.TryAcceptAsync(
                new ProcessedWebhookEvent { EventId = "evt_same", Type = "payment_intent.canceled", ReceivedAtUtc = at },
                Effect(Guid.NewGuid(), at),
                TestContext.Current.CancellationToken));

            // A refused delivery must not poison the context: the next distinct event has to be filed.
            Assert.True(await ledger.TryAcceptAsync(
                new ProcessedWebhookEvent { EventId = "evt_other", Type = "payment_intent.payment_failed", ReceivedAtUtc = at },
                Effect(Guid.NewGuid(), at),
                TestContext.Current.CancellationToken));
        }

        await using (var check = database.CreateContext())
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.Equal(2, await check.WebhookEvents.CountAsync(ct));
            Assert.Equal(2, await check.ReviewItems.CountAsync(ct));
        }
    }

    /// <summary>
    /// The item a failed payment files has to be openable from the queue. The delivery names the
    /// return in the provider's metadata, and the ledger resolves it in the same transaction, so the
    /// row points at the return instead of only quoting its number in a sentence.
    /// </summary>
    [Fact]
    public async Task A_delivery_naming_a_return_links_the_item_to_it()
    {
        await using var database = new LiveDatabase("rma_webhook_link");
        await database.InitializeAsync();

        var ct = TestContext.Current.CancellationToken;
        var at = DateTimeOffset.UtcNow;

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            FirstName = "Synthetic",
            LastName = "Customer",
            Email = "synthetic@example.test",
            PhoneNumber = "+10000000000",
            CreatedAtUtc = at,
        };

        var request = new RmaRequest
        {
            Id = Guid.NewGuid(),
            RmaNumber = "RMA-2026-70001",
            CustomerId = customer.Id,
            Status = RmaRequestStatus.Approved,
            Kind = RmaRequestKind.PaidRepair,
            CreatedAtUtc = at,
            UpdatedAtUtc = at,
        };

        await using (var setup = database.CreateContext())
        {
            await setup.Database.MigrateAsync(ct);
            setup.Customers.Add(customer);
            setup.RmaRequests.Add(request);
            await setup.SaveChangesAsync(ct);
        }

        await using (var db = database.CreateContext())
        {
            var ledger = new EfWebhookEventLedger(db);

            Assert.True(await ledger.TryAcceptAsync(
                new ProcessedWebhookEvent
                {
                    EventId = "evt_link",
                    Type = "payment_intent.payment_failed",
                    ReceivedAtUtc = at,
                },
                Effect(Guid.NewGuid(), at, "RMA-2026-70001"),
                ct));
        }

        await using (var check = database.CreateContext())
        {
            var item = await check.ReviewItems.SingleAsync(ct);

            Assert.Equal(request.Id, item.RmaRequestId);
            Assert.Equal(customer.Id, item.CustomerId);
        }
    }

    /// <summary>
    /// A number that matches nothing leaves the item unlinked rather than unmade: the payment still
    /// failed, and dropping the delivery over the link would trade a visible payment for a tidy
    /// foreign key.
    /// </summary>
    [Fact]
    public async Task A_delivery_naming_a_return_that_does_not_exist_is_still_filed()
    {
        await using var database = new LiveDatabase("rma_webhook_link_missing");
        await database.InitializeAsync();

        var ct = TestContext.Current.CancellationToken;
        var at = DateTimeOffset.UtcNow;

        await using (var setup = database.CreateContext())
        {
            await setup.Database.MigrateAsync(ct);
        }

        await using (var db = database.CreateContext())
        {
            var ledger = new EfWebhookEventLedger(db);

            Assert.True(await ledger.TryAcceptAsync(
                new ProcessedWebhookEvent
                {
                    EventId = "evt_link_missing",
                    Type = "payment_intent.payment_failed",
                    ReceivedAtUtc = at,
                },
                Effect(Guid.NewGuid(), at, "RMA-2026-79999"),
                ct));
        }

        await using (var check = database.CreateContext())
        {
            var item = await check.ReviewItems.SingleAsync(ct);

            Assert.Null(item.RmaRequestId);
            Assert.Null(item.CustomerId);
        }
    }

    /// <summary>
    /// The failure this contract exists for. Recorded first and filed second, a delivery whose filing
    /// failed — a deadlock, a restart, a context that will not save — leaves a row saying "handled"
    /// and no review item, and Stripe's retry is answered as a repeat. The payment is then never seen
    /// by a person, which is a worse outcome than the duplicate the ledger prevents.
    /// </summary>
    [Fact]
    public async Task A_delivery_whose_item_could_not_be_filed_is_not_recorded()
    {
        await using var database = new LiveDatabase("rma_webhook_failed_effect");
        await database.InitializeAsync();

        var ct = TestContext.Current.CancellationToken;
        var at = DateTimeOffset.UtcNow;
        var taken = Guid.NewGuid();

        await using (var setup = database.CreateContext())
        {
            await setup.Database.MigrateAsync(ct);

            // An item that already owns this id, so the effect below cannot be inserted.
            setup.ReviewItems.Add(Item(taken, at));
            await setup.SaveChangesAsync(ct);
        }

        var delivery = new ProcessedWebhookEvent
        {
            EventId = "evt_effect_fails",
            Type = "payment_intent.payment_failed",
            ReceivedAtUtc = at,
        };

        await using (var db = database.CreateContext())
        {
            var ledger = new EfWebhookEventLedger(db);

            await Assert.ThrowsAnyAsync<Exception>(() => ledger.TryAcceptAsync(delivery, Effect(taken, at), ct));
        }

        await using (var check = database.CreateContext())
        {
            Assert.Equal(0, await check.WebhookEvents.CountAsync(ct));
        }

        // The retry is answered as a first delivery, exactly as Stripe needs it to be, and its item
        // is filed.
        await using (var db = database.CreateContext())
        {
            var ledger = new EfWebhookEventLedger(db);

            Assert.True(await ledger.TryAcceptAsync(delivery, Effect(Guid.NewGuid(), at), ct));
        }

        await using (var check = database.CreateContext())
        {
            Assert.Equal(1, await check.WebhookEvents.CountAsync(ct));
            Assert.Equal(2, await check.ReviewItems.CountAsync(ct));
        }
    }
}
