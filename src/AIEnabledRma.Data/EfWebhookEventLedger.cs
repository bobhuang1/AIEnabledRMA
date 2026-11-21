using AIEnabledRma.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace AIEnabledRma.Data;

/// <summary>
/// The PostgreSQL implementation of <see cref="IWebhookEventLedger"/>. The primary key on
/// <c>event_id</c> is the lock: one statement inserts the delivery and reports whether it inserted
/// anything, so two retries racing each other cannot both answer "first".
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

    public async Task<bool> TryRecordAsync(
        string eventId,
        string type,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        var inserted = await db.Database.ExecuteSqlRawAsync(
            InsertOnce,
            [eventId, type, receivedAtUtc],
            cancellationToken);

        // One row inserted: this call recorded the delivery. Zero: it was already there.
        return inserted == 1;
    }
}
