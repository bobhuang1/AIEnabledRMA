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

    public string? ResolvedBy { get; set; }

    public DateTimeOffset? ResolvedAtUtc { get; set; }

    public string? Resolution { get; set; }
}

public static class ReviewSources
{
    public const string StartRefused = "start-human-review";
    public const string TriageLowConfidence = "triage-low-confidence";
    public const string PaymentFailed = "payment-failed";
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
        }, cancellationToken);

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
        }, cancellationToken);

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

        operations.AddAction(Action(operatorName, "resolve-review", itemId.ToString(), resolution, result.Succeeded));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<OperatorResult> OnRequestAsync(
        Guid rmaId,
        string operatorName,
        string action,
        string? detail,
        Func<RmaRequest, Task<OperatorResult>> apply,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);

        var request = await requests.GetByIdAsync(rmaId, cancellationToken);
        if (request is null)
        {
            operations.AddAction(Action(operatorName, action, rmaId.ToString(), detail, false));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new OperatorResult(false, "Return not found.");
        }

        var result = await apply(request);
        if (result.Succeeded)
        {
            request.UpdatedAtUtc = clock.UtcNow;
            await requests.UpdateAsync(request, cancellationToken);
        }

        operations.AddAction(Action(operatorName, action, request.RmaNumber, detail, result.Succeeded));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Operator {Operator} {Action} {Rma}: {Outcome}", operatorName, action, request.RmaNumber, result.Succeeded ? "ok" : "refused");
        return result;
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

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
