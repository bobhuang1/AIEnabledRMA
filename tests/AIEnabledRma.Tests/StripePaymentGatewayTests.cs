using System.Net;
using System.Text;
using AIEnabledRma.Ai;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// P9: Stripe behind <see cref="IPaymentGateway"/>, against a fake Stripe API (no network, no keys).
/// Keys below are synthetic placeholders.
/// </summary>
public sealed class StripePaymentGatewayTests
{
    private const string TestKey = "sk_test_synthetic_placeholder";

    private static (StripePaymentGateway Gateway, FakeStripe Api) Build(params string[] responses)
    {
        var api = new FakeStripe(responses);
        var gateway = new StripePaymentGateway(
            new HttpClient(api) { BaseAddress = new Uri("https://api.stripe.test/") },
            Options.Create(new StripeOptions { SecretKey = TestKey, WebhookSecret = "whsec_synthetic" }),
            NullLogger<StripePaymentGateway>.Instance);
        return (gateway, api);
    }

    private static PaymentAuthorizationRequest Fee(PaymentCaptureMode mode = PaymentCaptureMode.ManualLater) => new()
    {
        RmaNumber = "RMA-2026-00042",
        Amount = new Money(69m, "USD"),
        Purpose = PaymentPurpose.Deposit,
        CaptureMode = mode,
        CustomerEmail = "test@example.com",
        PaymentMethodToken = "pm_card_visa",
    };

    [Fact]
    public async Task A_repair_fee_is_authorised_for_manual_capture_with_an_idempotency_key()
    {
        var (gateway, api) = Build("""{"id":"pi_123","status":"requires_capture","amount":6900,"currency":"usd"}""");

        var result = await gateway.AuthorizeAsync(Fee(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("pi_123", result.Value!.TransactionId);
        Assert.Equal(PaymentStatus.Authorized, result.Value.Status);

        var call = Assert.Single(api.Calls);
        Assert.Equal("/v1/payment_intents", call.Path);
        Assert.Contains("amount=6900", call.Body, StringComparison.Ordinal);
        Assert.Contains("capture_method=manual", call.Body, StringComparison.Ordinal);
        Assert.Contains("confirm=true", call.Body, StringComparison.Ordinal);
        Assert.Contains("metadata%5Brma_number%5D=RMA-2026-00042", call.Body, StringComparison.Ordinal);
        Assert.Equal("rma:RMA-2026-00042:authorize:Deposit:6900:USD", call.IdempotencyKey);
        Assert.Equal("Bearer " + TestKey, call.Authorization);
    }

    [Fact]
    public async Task The_same_authorisation_twice_reuses_the_idempotency_key()
    {
        var (gateway, api) = Build(
            """{"id":"pi_1","status":"requires_capture"}""",
            """{"id":"pi_1","status":"requires_capture"}""");

        await gateway.AuthorizeAsync(Fee(), CancellationToken.None);
        await gateway.AuthorizeAsync(Fee(), CancellationToken.None);

        Assert.Equal(api.Calls[0].IdempotencyKey, api.Calls[1].IdempotencyKey);
    }

    [Fact]
    public async Task A_payment_needing_customer_action_is_cancelled_and_reported_as_a_failure()
    {
        var (gateway, api) = Build(
            """{"id":"pi_3ds","status":"requires_action"}""",
            """{"id":"pi_3ds","status":"canceled"}""");

        var result = await gateway.AuthorizeAsync(Fee(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("/v1/payment_intents/pi_3ds/cancel", api.Calls[1].Path);
    }

    [Fact]
    public async Task Capture_and_void_call_the_intent_endpoints()
    {
        var (gateway, api) = Build(
            """{"id":"pi_9","status":"succeeded","amount_received":6900,"currency":"usd","metadata":{"purpose":"Deposit"}}""",
            """{"id":"pi_8","status":"canceled"}""");

        var captured = await gateway.CaptureAsync("pi_9", CancellationToken.None);
        var voided = await gateway.VoidAsync("pi_8", CancellationToken.None);

        Assert.Equal(69m, captured.Value!.Amount.Amount);
        Assert.Equal(PaymentStatus.Captured, captured.Value.Status);
        Assert.True(voided.IsSuccess);
        Assert.Equal("/v1/payment_intents/pi_9/capture", api.Calls[0].Path);
        Assert.Equal("capture:pi_9", api.Calls[0].IdempotencyKey);
        Assert.Equal("/v1/payment_intents/pi_8/cancel", api.Calls[1].Path);
    }

    [Fact]
    public async Task A_stripe_error_becomes_a_failure_with_its_message()
    {
        var (gateway, _) = Build("!402:{\"error\":{\"message\":\"Your card was declined.\"}}");

        var result = await gateway.AuthorizeAsync(Fee(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Your card was declined.", result.ErrorMessage);
    }

    [Theory]
    [InlineData(69.00, "USD", 6900)]
    [InlineData(0.5, "EUR", 50)]
    [InlineData(1500, "JPY", 1500)]
    public void Amounts_convert_to_minor_units(decimal amount, string currency, long minor) =>
        Assert.Equal(minor, StripePaymentGateway.ToMinorUnits(new Money(amount, currency)));

    [Fact]
    public void Webhook_signatures_are_verified_with_a_replay_window()
    {
        const string secret = "whsec_synthetic";
        const string payload = """{"type":"payment_intent.canceled"}""";
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var t = now.ToUnixTimeSeconds();
        var header = $"t={t},v1={StripeWebhookSignature.Sign(payload, t, secret)}";

        Assert.True(StripeWebhookSignature.IsValid(payload, header, secret, now, 300));
        Assert.False(StripeWebhookSignature.IsValid(payload + " ", header, secret, now, 300));
        Assert.False(StripeWebhookSignature.IsValid(payload, header, "whsec_other", now, 300));
        Assert.False(StripeWebhookSignature.IsValid(payload, header, secret, now.AddMinutes(10), 300));
        Assert.False(StripeWebhookSignature.IsValid(payload, null, secret, now, 300));
    }

    [Fact]
    public void A_live_key_is_refused_outside_production()
    {
        using var factory = new StripeFactory("Development", "sk_live_synthetic_placeholder");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("sk_test_", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_stripe_configured_a_forged_webhook_is_rejected()
    {
        await using var factory = new StripeFactory("Development", TestKey);
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/payments/stripe/webhook",
            new StringContent("""{"type":"payment_intent.canceled"}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class StripeFactory(string environment, string key) : WebApplicationFactory<global::WebAppAnchor>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Rma", "Host=localhost;Database=unused;Username=x;Password=placeholder");
            builder.UseSetting("Payments:Provider", "stripe");
            builder.UseSetting("Payments:Stripe:SecretKey", key);
            builder.UseSetting("Payments:Stripe:WebhookSecret", "whsec_synthetic");
        }
    }

    private sealed record StripeCall(string Path, string Body, string? IdempotencyKey, string? Authorization);

    /// <summary>Returns the queued responses in order; "!402:{...}" returns that status.</summary>
    private sealed class FakeStripe(string[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<StripeCall> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(new StripeCall(
                request.RequestUri!.AbsolutePath,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
                request.Headers.Authorization?.ToString()));

            var raw = responses[Math.Min(_next++, responses.Length - 1)];
            var status = HttpStatusCode.OK;
            if (raw.StartsWith('!'))
            {
                status = (HttpStatusCode)int.Parse(raw[1..4], System.Globalization.CultureInfo.InvariantCulture);
                raw = raw[5..];
            }

            return new HttpResponseMessage(status) { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
        }
    }
}
