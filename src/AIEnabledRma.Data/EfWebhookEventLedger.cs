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
        ReviewItem? effect,
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
                db.ReviewItems.Add(effect);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }
}
