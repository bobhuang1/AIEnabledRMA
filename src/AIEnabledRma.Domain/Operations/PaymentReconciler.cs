using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Common;
using AIEnabledRma.Domain.Rma;
using Microsoft.Extensions.Logging;

namespace AIEnabledRma.Domain.Operations;

/// <summary>One row where this database and the payment provider disagree about a transaction.</summary>
public sealed record PaymentDisagreement(
    Guid RmaRequestId,
    string RmaNumber,
    string TransactionId,
    PaymentStatus Expected,
    PaymentStatus Provider,
    string LocalStatus);

/// <summary>What a reconciliation run did, in numbers an operator can act on.</summary>
public sealed record PaymentReconciliationReport(
    int Checked,
    int Agreed,
    IReadOnlyList<PaymentDisagreement> Disagreements,
    IReadOnlyList<string> Unanswered);

/// <summary>
/// Asks the payment provider what the stale authorisations actually became, and files a review item
/// where the answer is not what this database thinks.
/// </summary>
/// <remarks>
/// <para>
/// The webhook is the fast path: Stripe delivers <c>payment_intent.succeeded</c> and the local row
/// moves. Nothing survives a delivery that is lost, rejected or never configured — the return stays
/// "authorised" for good, the customer's money is not where the database says it is, and no row
/// records that anybody asked. This is the check that asks.
/// </para>
/// <para>
/// The expected state is derived from the return's own status rather than stored twice: a return in
/// repair or beyond is expected to be captured, a cancelled one is expected to be voided, and
/// anything earlier is expected to still be authorised. A row is a candidate only when nothing has
/// written it for <see cref="DefaultStaleAfter"/>; a delivery that arrived touches the row, so a row
/// nobody has touched is the shape a lost delivery leaves.
/// </para>
/// </remarks>
public sealed class PaymentReconciler(
    IRmaRepository requests,
    IPaymentGateway payments,
    IOperationsRepository operations,
    IReviewQueue reviews,
    IClock clock,
    ILogger<PaymentReconciler> logger)
{
    /// <summary>
    /// How long a row has to sit untouched before it is worth a provider call. Deliveries arrive in
    /// seconds; six hours is long enough that a slow one is not reported as lost, and short enough
    /// that an operator sees the drift the same working day.
    /// </summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromHours(6);

    public const int DefaultTake = 200;

    /// <summary>
    /// The open review items looked at to keep a second run from filing the same disagreement twice.
    /// Bounded on purpose: a queue longer than this is already the thing an operator is working
    /// through, and re-running reconciliation after a resolved item should file it again.
    /// </summary>
    private const int OpenItemsExamined = 500;

    public async Task<PaymentReconciliationReport> RunAsync(
        TimeSpan staleAfter,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleAfter, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);

        var cutoff = clock.UtcNow - staleAfter;
        var candidates = await requests.StalePaymentAuthorisationsAsync(cutoff, take, cancellationToken);

        var alreadyFiled = (await operations.OpenReviewItemsAsync(OpenItemsExamined, cancellationToken))
            .Where(item => item.Source == ReviewSources.PaymentMismatch && item.RmaRequestId is not null)
            .Select(item => item.RmaRequestId!.Value)
            .ToHashSet();

        var disagreements = new List<PaymentDisagreement>();
        var unanswered = new List<string>();
        var agreed = 0;

        foreach (var request in candidates)
        {
            var transactionId = request.PaymentTransactionId!;
            var asked = await payments.GetAsync(transactionId, cancellationToken);

            if (asked.IsFailure)
            {
                // An unanswered question is not a disagreement. Filing a review item for it would
                // put a transient provider error in the same queue as money that actually moved.
                unanswered.Add($"{request.RmaNumber} ({transactionId}): {asked.ErrorMessage}");
                continue;
            }

            var provider = asked.Value!.Status;
            var expected = ExpectedFor(request.Status);

            if (provider == expected)
            {
                agreed++;
                continue;
            }

            disagreements.Add(new PaymentDisagreement(
                request.Id,
                request.RmaNumber,
                transactionId,
                expected,
                provider,
                request.Status.ToString()));

            // One open item per return: a nightly run over the same lost delivery must not fill the
            // queue with the same sentence, and resolving the item is the operator saying they have
            // dealt with it, after which a later run may file it again.
            if (alreadyFiled.Contains(request.Id))
            {
                continue;
            }

            await reviews.AddAsync(
                new ReviewItem
                {
                    Id = Guid.NewGuid(),
                    CreatedAtUtc = clock.UtcNow,
                    Source = ReviewSources.PaymentMismatch,
                    Reason = PaymentMismatchReason.For(provider),
                    Explanation =
                        $"The payment provider reports {provider} for {transactionId}, but {request.RmaNumber} "
                        + $"is {request.Status} here and expects {expected}. A webhook may never have arrived; "
                        + "check the transaction before changing anything.",
                    RmaRequestId = request.Id,
                    CustomerId = request.CustomerId,
                },
                cancellationToken);

            logger.LogWarning(
                "Payment {TransactionId} for {Rma} is {Provider} at the provider and {Expected} here ({Status}); filed a review item",
                transactionId,
                request.RmaNumber,
                provider,
                expected,
                request.Status);
        }

        logger.LogInformation(
            "Payment reconciliation: {Checked} checked, {Agreed} agreed, {Disagreed} disagreed, {Unanswered} unanswered",
            candidates.Count,
            agreed,
            disagreements.Count,
            unanswered.Count);

        return new PaymentReconciliationReport(candidates.Count, agreed, disagreements, unanswered);
    }

    /// <summary>What this database's own status says the transaction should be.</summary>
    public static PaymentStatus ExpectedFor(RmaRequestStatus status) => status switch
    {
        RmaRequestStatus.InRepair
            or RmaRequestStatus.Repaired
            or RmaRequestStatus.ShippedToCustomer
            or RmaRequestStatus.Closed => PaymentStatus.Captured,

        // Cancellation voids the authorisation; a return rejected before the payment step carries no
        // transaction and never reaches here.
        RmaRequestStatus.Cancelled or RmaRequestStatus.Rejected => PaymentStatus.Voided,

        _ => PaymentStatus.Authorized,
    };
}
