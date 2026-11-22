using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Common;
using AIEnabledRma.Domain.Rules;
using Microsoft.Extensions.Logging;

namespace AIEnabledRma.Ai;

/// <summary>
/// The bundled fake payment gateway. No network, no account, no card handling.
///
/// It is a real implementation of <see cref="IPaymentGateway"/>, not a stub: it holds
/// authorisations in memory, honours the immediate-versus-manual-capture split, and records
/// every transaction so a developer can inspect exactly what the flow would have charged a
/// real provider.
///
/// Every positive amount is accepted. The gateway deliberately refuses nothing (short of a
/// negative amount), so the wizard's payment step lets every customer through; the product
/// decision is to exercise payment plumbing in development, not to simulate who gets approved.
/// </summary>
public sealed class FakePaymentGateway(
    ILogger<FakePaymentGateway> logger) : IPaymentGateway
{
    private readonly Dictionary<string, Authorization> _authorizations = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string ProviderName => "fake";

    /// <summary>
    /// A charge is required only when the amount is positive. A zero-charge warranty return
    /// must not reach a payment provider at all, so this short-circuits before anything else.
    /// </summary>
    public Task<bool> IsPaymentRequiredAsync(
        PaymentRequestContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(context.Total.IsPositive);

    public Task<Result<PaymentReceipt>> AuthorizeAsync(
        PaymentAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Amount.IsNegative)
        {
            return Task.FromResult(Result<PaymentReceipt>.Failure(
                "invalid_amount",
                "Payment amount cannot be negative."));
        }

        var id = $"fake_{Guid.NewGuid():N}"[..20];

        var status = request.CaptureMode == PaymentCaptureMode.Immediate
            ? PaymentStatus.Captured
            : PaymentStatus.Authorized;

        var receipt = new PaymentReceipt
        {
            TransactionId = id,
            Purpose = request.Purpose,
            Amount = request.Amount,
            Status = status,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        lock (_gate)
        {
            _authorizations[id] = new Authorization(receipt, request.CaptureMode);
        }

        logger.LogInformation(
            "Fake gateway {Action} {Amount} for RMA {RmaNumber} as {TransactionId}.",
            status == PaymentStatus.Captured ? "captured" : "authorised",
            request.Amount,
            request.RmaNumber,
            id);

        return Task.FromResult(Result<PaymentReceipt>.Success(receipt));
    }

    public Task<Result<PaymentReceipt>> CaptureAsync(
        string authorizationId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_authorizations.TryGetValue(authorizationId, out var existing))
            {
                return Task.FromResult(Result<PaymentReceipt>.Failure(
                    "unknown_authorization",
                    "That authorisation could not be found."));
            }

            if (existing.Receipt.Status == PaymentStatus.Captured)
            {
                return Task.FromResult(Result<PaymentReceipt>.Success(existing.Receipt));
            }

            var captured = existing.Receipt with { Status = PaymentStatus.Captured };
            _authorizations[authorizationId] = existing with { Receipt = captured };

            return Task.FromResult(Result<PaymentReceipt>.Success(captured));
        }
    }

    /// <summary>
    /// What the fake gateway would tell a reconciliation run. Its state is the in-memory record of
    /// what was authorised, captured or voided here, so a test can move money at the gateway without
    /// writing the application's row — which is exactly the state a lost webhook leaves behind.
    /// </summary>
    public Task<Result<PaymentReceipt>> GetAsync(string transactionId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_authorizations.TryGetValue(transactionId, out var known)
                ? Result<PaymentReceipt>.Success(known.Receipt)
                : Result<PaymentReceipt>.Failure(
                    "unknown_authorization",
                    "That authorisation could not be found."));
        }
    }

    /// <summary>
    /// Releases an authorisation and keeps the record, marked voided.
    /// </summary>
    /// <remarks>
    /// A real provider still answers for a cancelled intent, and reconciliation depends on that: a
    /// release the application never heard about is found by asking. Removing the row instead made
    /// the fake the one provider that forgets a void, which turned a reportable disagreement into an
    /// unanswerable question.
    /// </remarks>
    public Task<Result> VoidAsync(string authorizationId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_authorizations.TryGetValue(authorizationId, out var existing)
                && existing.Receipt.Status != PaymentStatus.Voided)
            {
                _authorizations[authorizationId] = existing
                    with { Receipt = existing.Receipt with { Status = PaymentStatus.Voided } };

                return Task.FromResult(Result.Success());
            }
        }

        // Already voided, or never known: releasing twice is not a second release.
        return Task.FromResult(Result.Failure(
            "unknown_authorization",
            "That authorisation could not be found."));
    }

    /// <summary>Everything the fake gateway has seen. Exposed for the diagnostics endpoint.</summary>
    public IReadOnlyList<PaymentReceipt> Transactions()
    {
        lock (_gate)
        {
            return _authorizations.Values.Select(a => a.Receipt).ToList();
        }
    }

    private sealed record Authorization(PaymentReceipt Receipt, PaymentCaptureMode Mode);
}

/// <summary>
/// Settings for the bundled development gateway, bound from <c>Payments:Fake</c>.
/// </summary>
public sealed class FakePaymentGatewayOptions
{
    public const string SectionName = "Payments:Fake";

    /// <summary>
    /// Allows the accept-everything gateway to be registered outside Development. Off by
    /// default: a deployment that forgets to replace it must fail to start rather than run a
    /// payment step that authorises every amount and collects nothing.
    /// </summary>
    public bool AllowInProduction { get; set; }
}
