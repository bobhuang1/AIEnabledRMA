using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Ai;

/// <summary>Stripe settings (plan D5). Keys come from the environment or a secret store only.</summary>
public sealed class StripeOptions
{
    public const string SectionName = "Payments:Stripe";

    /// <summary><c>sk_test_...</c> outside Production; the host refuses a live key there.</summary>
    public string? SecretKey { get; set; }

    /// <summary>Signing secret of the webhook endpoint (<c>whsec_...</c>).</summary>
    public string? WebhookSecret { get; set; }

    public string BaseUrl { get; set; } = "https://api.stripe.com/";

    /// <summary>Oldest webhook timestamp accepted, against replay.</summary>
    public int WebhookToleranceSeconds { get; set; } = 300;
}

/// <summary>
/// <see cref="IPaymentGateway"/> over Stripe PaymentIntents, through the REST API.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>A repair fee is authorised with <c>capture_method=manual</c>, matching "authorised now, captured once
///   the item is received and inspected"; an immediate charge uses automatic capture.</item>
///   <item>Every call carries an idempotency key derived from the RMA and the operation, so a retried request
///   (a double-clicked button, a network retry) can never authorise or capture twice.</item>
///   <item>A payment that needs further customer action (3-D Secure) is not completed here: it is cancelled and
///   reported as a failure, which the workflow sends to human review.</item>
/// </list>
/// </remarks>
public sealed class StripePaymentGateway(
    HttpClient http,
    IOptions<StripeOptions> options,
    ILogger<StripePaymentGateway> logger) : IPaymentGateway
{
    private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    public string ProviderName => "stripe";

    public Task<bool> IsPaymentRequiredAsync(PaymentRequestContext context, CancellationToken cancellationToken) =>
        Task.FromResult(context.Total.IsPositive);

    public async Task<Result<PaymentReceipt>> AuthorizeAsync(PaymentAuthorizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Amount.IsPositive)
        {
            return Result<PaymentReceipt>.Failure("invalid_amount", "Payment amount must be positive.");
        }

        if (string.IsNullOrWhiteSpace(request.PaymentMethodToken))
        {
            return Result<PaymentReceipt>.Failure("missing_payment_method", "No payment method was supplied.");
        }

        var minor = ToMinorUnits(request.Amount);
        var form = new List<KeyValuePair<string, string>>
        {
            new("amount", minor.ToString(CultureInfo.InvariantCulture)),
            new("currency", request.Amount.CurrencyCode.ToLowerInvariant()),
            new("payment_method", request.PaymentMethodToken),
            new("confirm", "true"),
            new("capture_method", request.CaptureMode == PaymentCaptureMode.ManualLater ? "manual" : "automatic"),
            new("payment_method_types[]", "card"),
            new("description", $"Return {request.RmaNumber}"),
            new("metadata[rma_number]", request.RmaNumber),
            new("metadata[purpose]", request.Purpose.ToString()),
        };

        var key = $"rma:{request.RmaNumber}:authorize:{request.Purpose}:{minor}:{request.Amount.CurrencyCode}";
        var (ok, body, error) = await PostAsync("v1/payment_intents", form, key, cancellationToken);
        if (!ok)
        {
            return Result<PaymentReceipt>.Failure("stripe_error", error);
        }

        var intent = body!.RootElement;
        var id = intent.GetProperty("id").GetString()!;
        var status = intent.GetProperty("status").GetString();

        switch (status)
        {
            case "requires_capture":
                return Result<PaymentReceipt>.Success(Receipt(id, request, PaymentStatus.Authorized));
            case "succeeded":
                return Result<PaymentReceipt>.Success(Receipt(id, request, PaymentStatus.Captured));
            default:
                // requires_action (3-D Secure) and the rest: not something this flow can finish.
                logger.LogWarning("Stripe PaymentIntent {Id} for {Rma} ended in {Status}; cancelling.", id, request.RmaNumber, status);
                await PostAsync($"v1/payment_intents/{Uri.EscapeDataString(id)}/cancel", [], $"cancel:{id}", cancellationToken);
                return Result<PaymentReceipt>.Failure("payment_not_completed", $"The payment needs further action ({status}). A specialist will contact you.");
        }
    }

    public async Task<Result<PaymentReceipt>> CaptureAsync(string authorizationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationId);

        var (ok, body, error) = await PostAsync(
            $"v1/payment_intents/{Uri.EscapeDataString(authorizationId)}/capture", [], $"capture:{authorizationId}", cancellationToken);
        if (!ok)
        {
            return Result<PaymentReceipt>.Failure("stripe_error", error);
        }

        var intent = body!.RootElement;
        var currency = intent.GetProperty("currency").GetString()!.ToUpperInvariant();
        var amount = FromMinorUnits(intent.GetProperty("amount_received").GetInt64(), currency);

        return Result<PaymentReceipt>.Success(new PaymentReceipt
        {
            TransactionId = authorizationId,
            Purpose = Enum.TryParse<PaymentPurpose>(
                intent.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("purpose", out var p) ? p.GetString() : null,
                out var purpose) ? purpose : PaymentPurpose.Deposit,
            Amount = amount,
            Status = PaymentStatus.Captured,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
    }

    public async Task<Result> VoidAsync(string authorizationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationId);

        var (ok, _, error) = await PostAsync(
            $"v1/payment_intents/{Uri.EscapeDataString(authorizationId)}/cancel",
            [new("cancellation_reason", "requested_by_customer")],
            $"cancel:{authorizationId}",
            cancellationToken);

        return ok ? Result.Success() : Result.Failure("stripe_error", error);
    }

    public static long ToMinorUnits(Money amount) =>
        ZeroDecimalCurrencies.Contains(amount.CurrencyCode)
            ? (long)decimal.Round(amount.Amount, 0, MidpointRounding.AwayFromZero)
            : (long)decimal.Round(amount.Amount * 100m, 0, MidpointRounding.AwayFromZero);

    public static Money FromMinorUnits(long minor, string currency) =>
        new(ZeroDecimalCurrencies.Contains(currency) ? minor : minor / 100m, currency);

    private static PaymentReceipt Receipt(string id, PaymentAuthorizationRequest request, PaymentStatus status) => new()
    {
        TransactionId = id,
        Purpose = request.Purpose,
        Amount = request.Amount,
        Status = status,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private async Task<(bool Ok, JsonDocument? Body, string Error)> PostAsync(
        string path,
        IReadOnlyList<KeyValuePair<string, string>> form,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.SecretKey);
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return (true, JsonDocument.Parse(text), string.Empty);
        }

        // Stripe's error message is shown to an operator or turned into a refusal; it never
        // contains the card number.
        string message;
        try
        {
            using var error = JsonDocument.Parse(text);
            message = error.RootElement.GetProperty("error").TryGetProperty("message", out var m)
                ? m.GetString() ?? "The payment provider refused the request."
                : "The payment provider refused the request.";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            message = "The payment provider refused the request.";
        }

        logger.LogWarning("Stripe {Path} failed with {Status}.", path, (int)response.StatusCode);
        return (false, null, message);
    }
}

/// <summary>Verifies the <c>Stripe-Signature</c> header of a webhook delivery.</summary>
public static class StripeWebhookSignature
{
    /// <summary>
    /// True when one of the header's <c>v1</c> signatures is the HMAC-SHA256 of
    /// <c>{t}.{payload}</c> under <paramref name="secret"/>, and <c>t</c> is within the tolerance.
    /// </summary>
    public static bool IsValid(string payload, string? header, string secret, DateTimeOffset now, int toleranceSeconds)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            if (kv[0].Trim() == "t" && long.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
            {
                timestamp = t;
            }
            else if (kv[0].Trim() == "v1")
            {
                signatures.Add(kv[1].Trim());
            }
        }

        if (timestamp is null || signatures.Count == 0
            || Math.Abs(now.ToUnixTimeSeconds() - timestamp.Value) > toleranceSeconds)
        {
            return false;
        }

        var expected = Sign(payload, timestamp.Value, secret);
        return signatures.Any(s =>
            s.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s), Encoding.ASCII.GetBytes(expected)));
    }

    public static string Sign(string payload, long timestamp, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp.ToString(CultureInfo.InvariantCulture)}.{payload}")));
    }
}
