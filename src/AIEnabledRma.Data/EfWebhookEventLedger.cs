using AIEnabledRma.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Data;

/// <summary>
/// The PostgreSQL implementation of <see cref="IWebhookEventLedger"/>. The primary key on
/// <c>event_id</c> is the lock: one statement inserts the delivery and reports whether it inserted
/// anything, so two retries racing each other cannot both answer "first". The same transaction then
/// files whatever the delivery asks for, so a delivery cannot be recorded as handled with its effect
/// missing.
/// </summary>
/// <remarks>
/// Written as SQL rather than through <c>Add</c> and a caught unique violation because a tracked
/// entity survives a successful save: the second delivery of the same event would then conflict in
/// the identity map before it ever reached the database, which is an exception the ledger is
/// supposed to answer with <c>false</c>. <c>ON CONFLICT DO NOTHING</c> is one round trip and leaves
/// no state behind.
/// </remarks>
public sealed class EfWebhookEventLedger(RmaDbContext db) : IWebhookEventLedger
{
    private const string InsertOnce =
        "INSERT INTO webhook_events (event_id, type, received_at_utc) VALUES ({0}, {1}, {2}) "
        + "ON CONFLICT (event_id) DO NOTHING";

    public async Task<bool> TryAcceptAsync(
        ProcessedWebhookEvent delivery,
        WebhookEffect? effect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        // The context enables retries, and EF refuses a user transaction outside the strategy: a
        // retried attempt has to take the whole thing again, insert included.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var inserted = await db.Database.ExecuteSqlRawAsync(
                InsertOnce,
                [delivery.EventId, delivery.Type, delivery.ReceivedAtUtc],
                cancellationToken);

            // Zero rows: an earlier delivery of this event already holds the row, and the effect it
            // filed is the only one that should exist.
            if (inserted != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            if (effect is not null)
            {
                await LinkAsync(effect, cancellationToken);

                db.ReviewItems.Add(effect.Item);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }

    /// <summary>
    /// Points the item at the return the delivery names, inside the transaction that files it.
    /// </summary>
    /// <remarks>
    /// A number that matches nothing leaves the item unlinked rather than unmade: a payment that
    /// failed is still a person's problem even when the return it mentions is gone or was never
    /// created, and refusing the delivery over the link would trade a visible payment for a tidy
    /// foreign key.
    /// </remarks>
    private async Task LinkAsync(WebhookEffect effect, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(effect.RmaNumber))
        {
            return;
        }

        var named = await db.RmaRequests
            .AsNoTracking()
            .Where(request => request.RmaNumber == effect.RmaNumber)
            .Select(request => new { request.Id, request.CustomerId })
            .FirstOrDefaultAsync(cancellationToken);

        if (named is null)
        {
            return;
        }

        effect.Item.RmaRequestId = named.Id;
        effect.Item.CustomerId = named.CustomerId;
    }
}
