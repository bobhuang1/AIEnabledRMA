using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Common;
using AIEnabledRma.Domain.Rma;
using Microsoft.Extensions.Logging;

namespace AIEnabledRma.Domain.Operations;

/// <summary>One row where this database and the payment provider disagree about a transaction.</summary>
/// <remarks>
/// The amount is part of the disagreement, not a footnote: a provider holding a different sum from
/// the one the return describes is money that moved unrecorded even when both sides call the state
/// "captured".
/// </remarks>
public sealed record PaymentDisagreement(
    Guid RmaRequestId,
    string RmaNumber,
    string TransactionId,
    PaymentStatus Expected,
    PaymentStatus Provider,
    string LocalStatus,
    Money ExpectedAmount,
    Money ProviderAmount);

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
/// The webhook is the fast path for a refusal: Stripe reports a failed or cancelled intent and a
/// delivery files it for a person. Nothing in this host moves a return's status from a delivery — a
/// capture is an operator's decision and a <c>succeeded</c> intent is its consequence — so a delivery
/// that is lost, rejected or never configured leaves the return saying "authorised" for good, the
/// customer's money not where the database says it is, and no row recording that anybody asked. This
/// is the check that asks, and it asks about the money as well as the state.
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

            var answer = asked.Value!;
            var expected = ExpectedFor(request.Status);
            var expectedAmount = AmountFor(request);

            if (Agrees(answer, expected, expectedAmount))
            {
                agreed++;
                continue;
            }

            disagreements.Add(new PaymentDisagreement(
                request.Id,
                request.RmaNumber,
                transactionId,
                expected,
                answer.Status,
                request.Status.ToString(),
                expectedAmount,
                answer.Amount));

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
                    Reason = PaymentMismatchReason.For(answer.Status),
                    Explanation =
                        // Two equal statuses and two different sums is still a disagreement, so the
                        // sentence says which half of it is wrong instead of repeating the states.
                        (answer.Status == expected
                            ? $"The payment provider holds {answer.Amount} for {transactionId}, where "
                              + $"{request.RmaNumber} says {expectedAmount}."
                            : $"The payment provider reports {answer.Status} holding {answer.Amount} for "
                              + $"{transactionId}, but {request.RmaNumber} is {request.Status} here and "
                              + $"expects {expected} of {expectedAmount}.")
                        + " A webhook may never have arrived; check the transaction before changing anything.",
                    // The two sums travel as fields as well as words: the row an operator judges the
                    // mismatch from has to carry the money, or "both sides say captured" reads clean.
                    ExpectedAmount = expectedAmount.Amount,
                    ExpectedCurrencyCode = expectedAmount.CurrencyCode,
                    ProviderAmount = answer.Amount.Amount,
                    ProviderCurrencyCode = answer.Amount.CurrencyCode,
                    RmaRequestId = request.Id,
                    CustomerId = request.CustomerId,
                },
                cancellationToken);

            logger.LogWarning(
                "Payment {TransactionId} for {Rma} is {Provider} at the provider holding {ProviderAmount} and {Expected} here holding {ExpectedAmount} ({Status}); filed a review item",
                transactionId,
                request.RmaNumber,
                answer.Status,
                answer.Amount,
                expected,
                expectedAmount,
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

    /// <summary>
    /// The sum this database's own row says the charge is: what the payment step authorised, in the
    /// row's currency. Read from the row rather than stored a second time, so a re-price that the
    /// provider never heard about is exactly what shows up as a disagreement.
    /// </summary>
    public static Money AmountFor(RmaRequest request) =>
        new(request.ShippingCharge + request.DepositAmount, request.CurrencyCode.Trim().ToUpperInvariant());

    /// <summary>
    /// Whether the provider's answer is the charge this row describes: the same state <em>and</em> the
    /// same money. Two equal statuses are not agreement — a capture for 69.00 where the return says
    /// 70.00 is money that moved unrecorded, and a status-only comparison calls it a match.
    /// </summary>
    public static bool Agrees(PaymentReceipt answer, PaymentStatus expected, Money amount) =>
        answer.Status == expected && SameMoney(answer.Amount, amount);

    /// <summary>
    /// Compares amount and currency. The code's case is a provider's formatting rather than a second
    /// currency, so <c>eur</c> and <c>EUR</c> are the same money.
    /// </summary>
    public static bool SameMoney(Money left, Money right) =>
        left.Amount == right.Amount
        && string.Equals(left.CurrencyCode, right.CurrencyCode, StringComparison.OrdinalIgnoreCase);
}
