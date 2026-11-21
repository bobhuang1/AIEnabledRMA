namespace AIEnabledRma.Domain.Operations;

/// <summary>
/// A Stripe delivery that has already been handled. Stripe retries a delivery until it gets a 2xx
/// and can deliver the same event more than once even after one, so the handler has to recognize a
/// repeat: without this, one failed payment files two review items and an operator resolves the same
/// case twice.
/// </summary>
public sealed class ProcessedWebhookEvent
{
    /// <summary>Stripe's event id (<c>evt_...</c>), which is stable across retries.</summary>
    public required string EventId { get; set; }

    /// <summary>The event type, kept for the operator reading the table.</summary>
    public required string Type { get; set; }

    public required DateTimeOffset ReceivedAtUtc { get; set; }
}

/// <summary>
/// Records a delivery and answers whether this call is the one that recorded it.
/// </summary>
/// <remarks>
/// The answer is decided by the database's primary key rather than by a read followed by a write:
/// two retries of one delivery can arrive at two request threads at the same time, and a
/// check-then-insert lets both see "not seen" and file the item twice.
/// </remarks>
public interface IWebhookEventLedger
{
    /// <summary>
    /// True when this is the first time the delivery was seen; false when it is a retry. Never
    /// throws for a duplicate.
    /// </summary>
    Task<bool> TryRecordAsync(
        string eventId,
        string type,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken);
}
