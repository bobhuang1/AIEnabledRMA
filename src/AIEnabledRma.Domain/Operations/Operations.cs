using System.ComponentModel.DataAnnotations.Schema;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Common;
using AIEnabledRma.Domain.Rma;
using Microsoft.Extensions.Logging;

namespace AIEnabledRma.Domain.Operations;

/// <summary>
/// Something a person must look at: a start the policy sent to human review (which creates no
/// return, so without this row it would be shown to the customer and then lost), a triage that
/// fell back or came back unsure, or a payment that could not be authorised.
/// </summary>
public sealed class ReviewItem
{
    public required Guid Id { get; set; }

    public required DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>See <see cref="ReviewSources"/>.</summary>
    public required string Source { get; set; }

    public required string Reason { get; set; }

    public string? Explanation { get; set; }

    /// <summary>Device serials involved (identify units, not people).</summary>
    public string? Serials { get; set; }

    public Guid? RmaRequestId { get; set; }

    public Guid? CustomerId { get; set; }

    public string Status { get; set; } = ReviewStatuses.Open;

    /// <summary>
    /// The provider's state, for the one kind of item that has one: a <see cref="ReviewSources.PaymentMismatch"/>.
    /// </summary>
    /// <remarks>
    /// Computed from <see cref="Reason"/> rather than stored beside it, so there is one copy of the
    /// fact and no column that can disagree with the code the queue was deduplicated on. The row is
    /// what an operator judges a mismatch from: "the provider says Captured" has to be a field on the
    /// dashboard, not a sentence they have to parse or a psql session they have to open.
    /// </remarks>
    [NotMapped]
    public PaymentStatus? ProviderStatus => PaymentMismatchReason.ProviderStatusIn(Reason);

    public string? ResolvedBy { get; set; }

    public DateTimeOffset? ResolvedAtUtc { get; set; }

    public string? Resolution { get; set; }

    /// <summary>
    /// Optimistic concurrency token, mapped to PostgreSQL's <c>xmin</c> system column. Resolving an
    /// item is a read followed by a write, so without it two operators who both have the item on
    /// screen are both told they resolved it and the second quietly replaces the first one's name
    /// and explanation — an outcome nothing in the audit trail would show.
    /// </summary>
    public uint RowVersion { get; set; }
}

public static class ReviewSources
{
    public const string StartRefused = "start-human-review";
    public const string PolicyHumanReview = "policy-human-review";
    public const string TriageLowConfidence = "triage-low-confidence";
    public const string PaymentFailed = "payment-failed";

    /// <summary>
    /// The provider and this database disagree about a transaction that already exists. Different
    /// from <see cref="PaymentFailed"/>, which is an authorisation the provider refused: nothing
    /// moved there, while here something may have moved unrecorded.
    /// </summary>
    public const string PaymentMismatch = "payment-mismatch";
}

/// <summary>
/// The reason code a payment-mismatch item carries, and the provider state encoded in it.
/// </summary>
/// <remarks>
/// The code is the item's machine-readable half: an earlier run looks for it to avoid filing the
/// same disagreement twice, and the dashboard reads the provider state back out of it. Keeping the
/// two in one string is what makes "the provider says X" impossible to have two versions of.
/// </remarks>
public static class PaymentMismatchReason
{
    public const string Prefix = "PaymentMismatch:";

    public static string For(PaymentStatus status) => Prefix + status;

    /// <summary>The provider state a reason code names, or null when it is not a mismatch code.</summary>
    public static PaymentStatus? ProviderStatusIn(string reason) =>
        reason.StartsWith(Prefix, StringComparison.Ordinal)
        && Enum.TryParse<PaymentStatus>(reason[Prefix.Length..], ignoreCase: false, out var status)
            ? status
            : null;
}

public static class ReviewStatuses
{
    public const string Open = "Open";
    public const string Resolved = "Resolved";
}

/// <summary>One row per operator action, written in the same transaction as the action.</summary>
public sealed class OperatorAction
{
    public required Guid Id { get; set; }

    public required DateTimeOffset AtUtc { get; set; }

    public required string Operator { get; set; }

    public required string Action { get; set; }

    /// <summary>The RMA number or review item id acted on.</summary>
    public required string Target { get; set; }

    public string? Detail { get; set; }

    public required bool Succeeded { get; set; }
}

/// <summary>Where the workflow files items for human review. Must never throw into the customer's step.</summary>
public interface IReviewQueue
{
    Task AddAsync(ReviewItem item, CancellationToken cancellationToken);
}

public interface IOperationsRepository
{
    Task<IReadOnlyList<ReviewItem>> OpenReviewItemsAsync(int take, CancellationToken cancellationToken);

    Task<ReviewItem?> GetReviewItemAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns waiting on an operator: shipped to us, received, or in repair.</summary>
    Task<IReadOnlyList<RmaRequest>> RequestsInStatusAsync(IReadOnlyCollection<RmaRequestStatus> statuses, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<OperatorAction>> RecentActionsAsync(int take, CancellationToken cancellationToken);

    void AddAction(OperatorAction action);
}

public sealed record OperatorResult(bool Succeeded, string Message);

/// <summary>
/// The operator's actions on a return after the customer is done: receive the item, inspect it,
/// then capture or void the authorised repair fee; and resolve review items. Every attempt, failed
/// or not, writes an <see cref="OperatorAction"/>.
/// </summary>
public sealed class OperatorService(
    IRmaRepository requests,
    IOperationsRepository operations,
    IPaymentGateway payments,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<OperatorService> logger)
{
    public Task<OperatorResult> MarkReceivedAsync(Guid rmaId, string operatorName, CancellationToken cancellationToken) =>
        OnRequestAsync(rmaId, operatorName, "receive", null, async request =>
        {
            if (request.Status != RmaRequestStatus.AwaitingShipment)
            {
                return new OperatorResult(false, $"Only a return awaiting shipment can be received (it is {request.Status}).");
            }

            request.Status = RmaRequestStatus.Received;
            return new OperatorResult(true, "Marked as received.");
        }, cancellationToken);

    /// <summary>Captures the repair fee authorised at the payment step, after inspection.</summary>
    public Task<OperatorResult> CaptureAsync(Guid rmaId, string operatorName, string inspectionNote, CancellationToken cancellationToken) =>
        OnRequestAsync(rmaId, operatorName, "capture", inspectionNote, async request =>
        {
            if (string.IsNullOrWhiteSpace(inspectionNote))
            {
                return new OperatorResult(false, "Record what the inspection found before capturing.");
            }

            if (request.Status != RmaRequestStatus.Received)
            {
                return new OperatorResult(false, $"Capture happens after the item is received and inspected (it is {request.Status}).");
            }

            if (string.IsNullOrEmpty(request.PaymentTransactionId) || request.DepositAmount <= 0)
            {
                return new OperatorResult(false, "This return has no authorised repair fee to capture.");
            }

            var captured = await payments.CaptureAsync(request.PaymentTransactionId, cancellationToken);
            if (captured.IsFailure)
            {
                return new OperatorResult(false, captured.ErrorMessage ?? "The payment provider refused the capture.");
            }

            request.Status = RmaRequestStatus.InRepair;
            return new OperatorResult(true, $"Captured {request.DepositAmount:0.00} {request.CurrencyCode}; moved to repair.");
        }, cancellationToken, moneyMoves: true);

    /// <summary>Voids the authorisation and cancels the return, with a reason.</summary>
    public Task<OperatorResult> VoidAsync(Guid rmaId, string operatorName, string reason, CancellationToken cancellationToken) =>
        OnRequestAsync(rmaId, operatorName, "void", reason, async request =>
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return new OperatorResult(false, "Give a reason for voiding.");
            }

            if (request.Status is RmaRequestStatus.InRepair or RmaRequestStatus.Repaired or RmaRequestStatus.ShippedToCustomer
                or RmaRequestStatus.Closed or RmaRequestStatus.Cancelled)
            {
                return new OperatorResult(false, $"A return in {request.Status} cannot be voided.");
            }

            if (!string.IsNullOrEmpty(request.PaymentTransactionId))
            {
                var voided = await payments.VoidAsync(request.PaymentTransactionId, cancellationToken);
                if (voided.IsFailure)
                {
                    return new OperatorResult(false, voided.ErrorMessage ?? "The payment provider refused the void.");
                }
            }

            request.Status = RmaRequestStatus.Cancelled;
            request.CancellationReason = "Voided by an operator: " + Truncate(reason, 400);
            return new OperatorResult(true, "Authorisation voided and return cancelled.");
        }, cancellationToken, moneyMoves: true);

    public async Task<OperatorResult> ResolveReviewItemAsync(Guid itemId, string operatorName, string resolution, CancellationToken cancellationToken)
    {
        var item = await operations.GetReviewItemAsync(itemId, cancellationToken);
        OperatorResult result;
        if (item is null)
        {
            result = new OperatorResult(false, "Review item not found.");
        }
        else if (string.IsNullOrWhiteSpace(resolution))
        {
            result = new OperatorResult(false, "Record how the item was resolved.");
        }
        else if (item.Status != ReviewStatuses.Open)
        {
            result = new OperatorResult(false, "This item is already resolved.");
        }
        else
        {
            item.Status = ReviewStatuses.Resolved;
            item.ResolvedBy = operatorName;
            item.ResolvedAtUtc = clock.UtcNow;
            item.Resolution = Truncate(resolution, 2000);
            result = new OperatorResult(true, "Resolved.");
        }

        var recorded = Action(operatorName, "resolve-review", itemId.ToString(), resolution, result.Succeeded);
        operations.AddAction(recorded);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Both operators held the open item, so the status guard on the stale copy still said
            // Open and only the token can tell them apart. The resolution and this action row were
            // rolled back together; the attempt is recorded again as the refusal it was, and the
            // message must not suggest the item is unresolved when the other operator's write stands.
            recorded.Succeeded = false;
            recorded.Detail = resolution is null
                ? ConflictDetail
                : Truncate(resolution + " " + ConflictDetail, 2000);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogWarning(
                "Operator {Operator} resolve-review {Item}: refused, the item changed since it was read",
                operatorName,
                itemId);

            return new OperatorResult(false, ConflictReviewMessage);
        }

        return result;
    }

    private async Task<OperatorResult> OnRequestAsync(
        Guid rmaId,
        string operatorName,
        string action,
        string? detail,
        Func<RmaRequest, Task<OperatorResult>> apply,
        CancellationToken cancellationToken,
        bool moneyMoves = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);

        var request = await requests.GetByIdAsync(rmaId, cancellationToken);
        if (request is null)
        {
            operations.AddAction(Action(operatorName, action, rmaId.ToString(), detail, false));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new OperatorResult(false, "Return not found.");
        }

        if (moneyMoves)
        {
            return await GuardedAsync(
                request, operatorName, action, detail, apply, cancellationToken);
        }

        var result = await apply(request);
        if (result.Succeeded)
        {
            // The repository loads the aggregate tracked, so the save below writes it.
            request.UpdatedAtUtc = clock.UtcNow;
        }

        var recorded = Action(operatorName, action, request.RmaNumber, detail, result.Succeeded);
        operations.AddAction(recorded);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // The row changed after this request loaded it. The status write and the action row
            // were rolled back together, so the attempt is recorded again — as a refusal. An
            // audit trail that showed this one as successful would be the exact defect the
            // token is here to close.
            recorded.Succeeded = false;
            recorded.Detail = detail is null
                ? ConflictDetail
                : Truncate(detail + " " + ConflictDetail, 2000);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogWarning(
                "Operator {Operator} {Action} {Rma}: refused, the return changed since it was read",
                operatorName,
                action,
                request.RmaNumber);

            return new OperatorResult(false, ConflictMessage);
        }

        logger.LogInformation("Operator {Operator} {Action} {Rma}: {Outcome}", operatorName, action, request.RmaNumber, result.Succeeded ? "ok" : "refused");
        return result;
    }

    /// <summary>
    /// A transition that moves money, run under a row lock that proves the return is still the one the
    /// operator read.
    /// </summary>
    /// <remarks>
    /// Capturing charges and voiding releases, both before the row is written, and the version token
    /// only refuses the write — which arrives after the provider has already acted, for a return
    /// another operator had voided or captured in the meantime. So the proof is a lock and it comes
    /// first: a stale page is refused here, with nothing sent to the provider, and the operator is
    /// told the money did not move because it did not. On the other side of the same lock the charge
    /// and its write commit together, so the row cannot drift out from under a capture that stands.
    /// </remarks>
    private async Task<OperatorResult> GuardedAsync(
        RmaRequest request,
        string operatorName,
        string action,
        string? detail,
        Func<RmaRequest, Task<OperatorResult>> apply,
        CancellationToken cancellationToken)
    {
        OperatorResult? outcome = null;

        var committed = await requests.TryRunGuardedAsync(
            request,
            async () =>
            {
                outcome = await apply(request);

                if (outcome.Succeeded)
                {
                    request.UpdatedAtUtc = clock.UtcNow;
                }

                // The action row joins the same transaction as the status change: a capture that
                // committed without its audit line would be a charge nobody can account for.
                operations.AddAction(Action(operatorName, action, request.RmaNumber, detail, outcome.Succeeded));
            },
            cancellationToken);

        if (!committed)
        {
            // Nothing was charged and nothing was released, so the attempt is the only thing to
            // record — written on its own, outside the transition that was refused.
            var refused = Action(operatorName, action, request.RmaNumber, detail, false);
            refused.Detail = detail is null
                ? ConflictDetail
                : Truncate(detail + " " + ConflictDetail, 2000);
            operations.AddAction(refused);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogWarning(
                "Operator {Operator} {Action} {Rma}: refused before the payment provider was called, the return changed since it was read",
                operatorName,
                action,
                request.RmaNumber);

            return new OperatorResult(false, ConflictMessage);
        }

        logger.LogInformation("Operator {Operator} {Action} {Rma}: {Outcome}", operatorName, action, request.RmaNumber, outcome!.Succeeded ? "ok" : "refused");
        return outcome!;
    }

    private OperatorAction Action(string operatorName, string action, string target, string? detail, bool succeeded) => new()
    {
        Id = Guid.NewGuid(),
        AtUtc = clock.UtcNow,
        Operator = Truncate(operatorName, 256),
        Action = action,
        Target = Truncate(target, 64),
        Detail = detail is null ? null : Truncate(detail, 2000),
        Succeeded = succeeded,
    };

    /// <summary>What the operator reads when another writer got there first.</summary>
    private const string ConflictMessage =
        "Another person changed this return while you were working on it, so nothing was saved. "
        + "Reload the return and check its current state before trying again.";

    /// <summary>What the operator reads when another one resolved the item first.</summary>
    private const string ConflictReviewMessage =
        "Another person changed this item while you were looking at it, so nothing was saved. "
        + "Reload the queue and check its current state before trying again.";

    private const string ConflictDetail = "refused: the return changed since it was read";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
