using System.Net;
using System.Text;
using AIEnabledRma.Ai;
using AIEnabledRma.Domain.Operations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AIEnabledRma.Tests;

/// <summary>
/// Stripe retries a delivery until it is answered 2xx and can deliver the same event more than once
/// even after that. The handler must recognise the repeat: one failed payment that files two review
/// items is a case an operator resolves twice, and the second resolution is about a row that no
/// longer needs one.
/// </summary>
/// <remarks>
/// The ledger is replaced with an in-memory recorder so the assertion is about the handler's
/// decision rather than about PostgreSQL. <see cref="WebhookEventLedgerTests"/> covers the
/// implementation the host registers, including what happens when the effect cannot be filed.
/// </remarks>
public sealed class WebhookDedupeTests
{
    private const string WebhookSecret = "whsec_synthetic";

    private const string CanceledIntent = """
        {"id":"evt_test_retry","type":"payment_intent.canceled","data":{"object":{"id":"pi_test_1","metadata":{"rma_number":"RMA-2026-00001"}}}}
        """;

    [Fact]
    public async Task A_retried_delivery_files_one_review_item()
    {
        var ledger = new RecordingLedger();

        await using var factory = new StripeFactory(ledger);
        using var client = factory.CreateClient();

        var first = await PostSignedAsync(client, CanceledIntent);
        var second = await PostSignedAsync(client, CanceledIntent);

        // Both deliveries are acknowledged: refusing the retry would make Stripe keep trying a
        // delivery that has already been handled.
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Single(ledger.Items);
        Assert.Equal(2, ledger.Attempts);
    }

    /// <summary>
    /// The handler is the only place that reads the provider's metadata, so it is the only place
    /// that can tell the ledger which return a delivery is about. Losing the number here loses the
    /// link, whatever the ledger does with it afterwards.
    /// </summary>
    [Fact]
    public async Task A_delivery_passes_the_return_it_names_to_the_ledger()
    {
        var ledger = new RecordingLedger();

        await using var factory = new StripeFactory(ledger);
        using var client = factory.CreateClient();

        await PostSignedAsync(client, CanceledIntent);

        Assert.Equal("RMA-2026-00001", Assert.Single(ledger.Effects).RmaNumber);
    }

    [Fact]
    public async Task A_delivery_of_a_different_event_is_still_processed()
    {
        var ledger = new RecordingLedger();

        await using var factory = new StripeFactory(ledger);
        using var client = factory.CreateClient();

        await PostSignedAsync(client, CanceledIntent);
        await PostSignedAsync(client, CanceledIntent.Replace("evt_test_retry", "evt_test_other"));

        Assert.Equal(2, ledger.Items.Count);
    }

    /// <summary>
    /// Without an id there is nothing to deduplicate on, so a retry is indistinguishable from a new
    /// event. Refusing the delivery is the fail-closed answer: Stripe keeps the event queued and an
    /// operator can replay it, where acting twice cannot be undone.
    /// </summary>
    [Fact]
    public async Task A_delivery_without_an_event_id_is_refused()
    {
        var ledger = new RecordingLedger();

        await using var factory = new StripeFactory(ledger);
        using var client = factory.CreateClient();

        var response = await PostSignedAsync(
            client,
            """{"type":"payment_intent.canceled","data":{"object":{"id":"pi_test_1"}}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(ledger.Items);
    }

    /// <summary>
    /// A signed payload this host cannot read is answered 400, not 500: Stripe retries a 500 until
    /// it gives up, and an event that keeps failing is one nobody ever looks at.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("""{"id":"evt_no_type"}""")]
    [InlineData("""{"id":"evt_bad_data","type":"payment_intent.payment_failed"}""")]
    public async Task A_signed_payload_the_host_cannot_read_is_refused(string payload)
    {
        var ledger = new RecordingLedger();

        await using var factory = new StripeFactory(ledger);
        using var client = factory.CreateClient();

        var response = await PostSignedAsync(client, payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(ledger.Items);
    }

    private static Task<HttpResponseMessage> PostSignedAsync(HttpClient client, string payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, "/payments/stripe/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(
            "Stripe-Signature",
            $"t={timestamp},v1={StripeWebhookSignature.Sign(payload, timestamp, WebhookSecret)}");

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The first delivery of an event records it and keeps the item; a retry records the attempt and
    /// keeps nothing, which is the whole contract the handler relies on.
    /// </summary>
    private sealed class RecordingLedger : IWebhookEventLedger
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public int Attempts { get; private set; }

        public List<ReviewItem> Items { get; } = [];

        public List<WebhookEffect> Effects { get; } = [];

        public Task<bool> TryAcceptAsync(
            ProcessedWebhookEvent delivery,
            WebhookEffect? effect,
            CancellationToken cancellationToken)
        {
            Attempts++;

            if (!_seen.Add(delivery.EventId))
            {
                return Task.FromResult(false);
            }

            if (effect is not null)
            {
                Effects.Add(effect);
                Items.Add(effect.Item);
            }

            return Task.FromResult(true);
        }
    }

    private sealed class StripeFactory(RecordingLedger ledger)
        : WebApplicationFactory<global::WebAppAnchor>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Rma", "Host=localhost;Database=unused;Username=x;Password=placeholder");
            builder.UseSetting("Payments:Provider", "stripe");
            builder.UseSetting("Payments:Stripe:SecretKey", "sk_test_synthetic");
            builder.UseSetting("Payments:Stripe:WebhookSecret", WebhookSecret);

            // Registered after the host's own, which is what the request resolves: the default
            // registration would open PostgreSQL for the ledger.
            builder.ConfigureServices(services => services.AddSingleton<IWebhookEventLedger>(ledger));
        }
    }
}
