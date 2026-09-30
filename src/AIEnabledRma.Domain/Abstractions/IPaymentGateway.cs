using AIEnabledRma.Domain.Common;

namespace AIEnabledRma.Domain.Abstractions;

/// <summary>
/// Provider-agnostic payment boundary. The RMA flow depends only on this interface, so the
/// bundled fake gateway means a developer can run the whole wizard with no external account,
/// and a real provider is a drop-in replacement.
/// </summary>
public interface IPaymentGateway
{
    string ProviderName { get; }

    /// <summary>True when a payment is actually required; the wizard skips billing otherwise.</summary>
    Task<bool> IsPaymentRequiredAsync(PaymentRequestContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Authorises a charge. In this flow a deposit is authorised up front and captured once the
    /// faulty unit is received, whereas shipping is captured immediately, so
    /// <paramref name="captureMode"/> matters.
    /// </summary>
    Task<Result<PaymentReceipt>> AuthorizeAsync(PaymentAuthorizationRequest request, CancellationToken cancellationToken);

    /// <summary>Captures a previously authorised amount. Used for the deposit path.</summary>
    Task<Result<PaymentReceipt>> CaptureAsync(string authorizationId, CancellationToken cancellationToken);

    /// <summary>Releases an authorisation without taking money. Used on RMA cancellation.</summary>
    Task<Result> VoidAsync(string authorizationId, CancellationToken cancellationToken);
}

/// <summary>
/// Which charge a payment relates to. Drives capture behaviour in the fake gateway and in
/// the documented Stripe adapter.
/// </summary>
public enum PaymentCaptureMode
{
    /// <summary>Charge now, in full.</summary>
    Immediate = 0,

    /// <summary>Authorise now, capture later when the faulty unit arrives.</summary>
    ManualLater = 1,
}

public enum PaymentPurpose
{
    Shipping = 0,
    Deposit = 1,
    RepairFee = 2,
}

public sealed record PaymentRequestContext
{
    public Money Total { get; init; }
}

public sealed record PaymentAuthorizationRequest
{
    public required string RmaNumber { get; init; }

    public required Money Amount { get; init; }

    public required PaymentPurpose Purpose { get; init; }

    public required PaymentCaptureMode CaptureMode { get; init; }

    public required string CustomerEmail { get; init; }

    public string? PaymentMethodToken { get; init; }

    public IReadOnlyList<string> ReferenceNumbers { get; init; } = [];
}

public sealed record PaymentReceipt
{
    public required string TransactionId { get; init; }

    public required PaymentPurpose Purpose { get; init; }

    public required Money Amount { get; init; }

    public required PaymentStatus Status { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }
}

public enum PaymentStatus
{
    Authorized = 0,
    Captured = 1,
    Voided = 2,
    Failed = 3,
}
