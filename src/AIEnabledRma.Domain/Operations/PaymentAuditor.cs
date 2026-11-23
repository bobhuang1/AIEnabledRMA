using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Common;
using AIEnabledRma.Domain.Rma;
using Microsoft.Extensions.Logging;

namespace AIEnabledRma.Domain.Operations;

/// <summary>Which side holds a capture or a void that the other one has no record of.</summary>
public enum PaymentDriftDirection
{
    /// <summary>The provider holds a capture or a void; the return's status has no record of it.</summary>
    ProviderOnly = 0,

    /// <summary>The return's status says captured or voided; the provider does not hold that.</summary>
    RowOnly = 1,

    /// <summary>
    /// Neither side is simply missing one: both hold a terminal state and they are not the same, or
    /// the provider reports a state the return's status cannot express at all.
    /// </summary>
    Mismatched = 2,
}

/// <summary>One return where this database and the payment provider disagree about a transaction.</summary>
/// <remarks>
/// The amounts travel with the drift because they are half of what an operator has to decide: the
/// same status on both sides with a different sum is a capture this database recorded wrong, not a
/// clean row.
/// </remarks>
public sealed record PaymentDrift(
    Guid RmaRequestId,
    string RmaNumber,
    string TransactionId,
    PaymentDriftDirection Direction,
    PaymentStatus Expected,
    PaymentStatus Provider,
    string LocalStatus,
    Money ExpectedAmount,
    Money ProviderAmount);

/// <summary>What one audit run saw, in numbers an operator can act on.</summary>
public sealed record PaymentAuditReport(
    int Checked,
    int InStep,
    IReadOnlyList<PaymentDrift> Drift,
    IReadOnlyList<string> Unanswered,
    bool More);

/// <summary>
/// Asks the provider what every transaction this database remembers actually became, and says which
/// side is holding a capture or a void the other one never recorded.
/// </summary>
/// <remarks>
/// <para>
/// This is the sweep beside <see cref="PaymentReconciler"/> rather than a second opinion on it. The
/// reconciler works a stale row that still looks authorised and files a review item so somebody
/// acts; it deliberately skips anything touched inside its window, because a delivery that arrived
/// writes the row and a recent write is somebody else's business. The case this exists for looks
/// the opposite way round: an operator captured, the provider took the money, and the write that
/// would have recorded it lost the row race — so the row still sits at <c>Received</c> and has just
/// been written, which is exactly what the reconciler's window skips.
/// </para>
/// <para>
/// Nothing is filed and nothing is changed here. A drift needs a person to look at the provider's
/// dashboard, decide which side is wrong, and act; a command that both found the problem and edited
/// the row would be a second, unlogged operator. <c>reconcile-payments</c> is the one that files.
/// </para>
/// <para>
/// The expectation comes from <see cref="PaymentReconciler.ExpectedFor"/> so the two commands cannot
/// disagree about what a status means.
/// </para>
/// </remarks>
public sealed class PaymentAuditor(
    IRmaRepository requests,
    IPaymentGateway payments,
    ILogger<PaymentAuditor> logger)
{
    public async Task<PaymentAuditReport> RunAsync(int? take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(take ?? 1, 0);

        // One row past the bound: that is how a bounded run can tell it cut the set short instead of
        // having compared every return. Unbounded, the extra row is simply the end of the list.
        var rows = await requests.PaymentTransactionsAsync(
            take is { } bound ? bound + 1 : null,
            cancellationToken);

        var more = false;
        if (take is { } limit)
        {
            more = rows.Count > limit;

            if (more)
            {
                rows = rows.Take(limit).ToList();
            }
        }

        var drift = new List<PaymentDrift>();
        var unanswered = new List<string>();
        var inStep = 0;

        foreach (var request in rows)
        {
            var transactionId = request.PaymentTransactionId!;
            var asked = await payments.GetAsync(transactionId, cancellationToken);

            if (asked.IsFailure)
            {
                // A question the provider could not answer is not a drift; reporting it as one would
                // put a transient error beside money that actually moved. It is still not agreement.
                unanswered.Add($"{request.RmaNumber} ({transactionId}): {asked.ErrorMessage}");
                continue;
            }

            var answer = asked.Value!;
            var expected = PaymentReconciler.ExpectedFor(request.Status);
            var expectedAmount = PaymentReconciler.AmountFor(request);

            if (PaymentReconciler.Agrees(answer, expected, expectedAmount))
            {
                inStep++;
                continue;
            }

            var direction = Classify(expected, answer.Status);
            drift.Add(new PaymentDrift(
                request.Id,
                request.RmaNumber,
                transactionId,
                direction,
                expected,
                answer.Status,
                request.Status.ToString(),
                expectedAmount,
                answer.Amount));

            logger.LogWarning(
                "Payment {TransactionId} for {Rma}: provider says {Provider} holding {ProviderAmount}, this database expects {Expected} holding {ExpectedAmount} ({Status}); {Direction}",
                transactionId,
                request.RmaNumber,
                answer.Status,
                answer.Amount,
                expected,
                expectedAmount,
                request.Status,
                direction);
        }

        logger.LogInformation(
            "Payment audit: {Checked} checked, {InStep} in step, {Drifted} drifted, {Unanswered} unanswered, bound {Bound}",
            rows.Count,
            inStep,
            drift.Count,
            unanswered.Count,
            take?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");

        return new PaymentAuditReport(rows.Count, inStep, drift, unanswered, more);
    }

    /// <summary>
    /// Which side is holding something the other one has not got. A capture or a void is what an
    /// operator sees as money having moved, so those are the states that count as "holding" one; two
    /// different ones, or a failed provider attempt beside a live authorisation, are a mismatch
    /// rather than a missing write, because neither side can be called the stale one.
    /// </summary>
    public static PaymentDriftDirection Classify(PaymentStatus expected, PaymentStatus provider) =>
        (Holds(expected), Holds(provider)) switch
        {
            (false, true) => PaymentDriftDirection.ProviderOnly,
            (true, false) => PaymentDriftDirection.RowOnly,
            _ => PaymentDriftDirection.Mismatched,
        };

    private static bool Holds(PaymentStatus status) =>
        status is PaymentStatus.Captured or PaymentStatus.Voided;
}
