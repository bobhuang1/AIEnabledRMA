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
/// What a delivery asks this host to file, and the return it names when the provider's metadata
/// carries one.
/// </summary>
/// <remarks>
/// The number travels with the effect rather than on <see cref="ProcessedWebhookEvent"/> because it
/// is not a fact about the row that deduplicates a delivery: it is how the item is to be filed, and
/// the ledger resolves it to a return inside the same transaction. Keeping it off the entity is also
/// what lets the row's schema stay what it says it is — a record of which deliveries arrived.
/// </remarks>
public sealed record WebhookEffect(ReviewItem Item, string? RmaNumber);

/// <summary>
/// Records a delivery and, when this call is the one that records it, files what it asks for —
/// answering whether the caller's effect was applied.
/// </summary>
/// <remarks>
/// The answer is decided by the database's primary key rather than by a read followed by a write:
/// two retries of one delivery can arrive at two request threads at the same time, and a
/// check-then-insert lets both see "not seen" and file the item twice.
/// </remarks>
public interface IWebhookEventLedger
{
    /// <summary>
    /// True when this is the first time the delivery was seen and <paramref name="effect"/> was
    /// filed — including the return it names, when it names one; false when it is a retry, in which
    /// case nothing is filed. Never throws for a duplicate.
    /// </summary>
    /// <remarks>
    /// A delivery that names a return this database does not have still files its item: a payment
    /// that failed is a person's problem whether or not the return still exists, and the item is
    /// filed unlinked rather than withheld.
    /// </remarks>
    /// <remarks>
    /// The delivery and its effect are one transaction because the alternative loses money quietly.
    /// Recorded first and filed second, a delivery whose filing failed — a deadlock, a restart, a
    /// context that will not save — leaves a row saying "handled" and no review item at all, and
    /// Stripe's retry is answered as a repeat, so the failed payment is never seen by a person. The
    /// ledger is there to prevent a duplicate, not to trade it for a disappearance.
    /// </remarks>
    Task<bool> TryAcceptAsync(
        ProcessedWebhookEvent delivery,
        WebhookEffect? effect,
        CancellationToken cancellationToken);
}
