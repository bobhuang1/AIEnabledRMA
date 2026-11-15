using System.Net;
using AIEnabledRma.Ai;
using AIEnabledRma.Rag;
using AIEnabledRma.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// The wizard's write limit partitions on <c>Connection.RemoteIpAddress</c>, which behind a
/// reverse proxy is the proxy: every customer in one partition, throttled together. Believing
/// <c>X-Forwarded-For</c> from anyone fixes that and creates a worse problem, because the header
/// is caller-controlled and a fresh address per request is an unlimited amount of budget. These
/// cases pin both halves: a configured proxy may set the address, and nothing else may.
/// </summary>
public sealed class TrustedProxyTests
{
    private static readonly IPAddress Proxy = IPAddress.Parse("127.0.0.1");
    private static readonly IPAddress Customer = IPAddress.Parse("203.0.113.9");
    private static readonly IPAddress Stranger = IPAddress.Parse("198.51.100.7");

    private const string FirstCustomer = "203.0.113.10";
    private const string SecondCustomer = "203.0.113.11";

    [Fact]
    public void Without_a_configured_proxy_the_frameworks_loopback_trust_is_removed()
    {
        var options = new TrustedProxyOptions().ToForwardedHeadersOptions();

        // The framework trusts loopback by default. Left in place, "no proxy configured" would
        // still mean "a process on this machine may choose the client address".
        Assert.Empty(options.KnownProxies);
        Assert.Empty(options.KnownIPNetworks);

        // Only the client address is rewritten. Scheme and host are not this host's business.
        Assert.Equal(ForwardedHeaders.XForwardedFor, options.ForwardedHeaders);
    }

    [Fact]
    public void A_proxy_address_that_is_not_an_ip_fails_loudly()
    {
        var options = new TrustedProxyOptions { TrustedProxies = ["10.0.0.0/8"] };

        // Silently dropping the entry would put every customer back in one partition, which is
        // the failure this configuration exists to prevent.
        var thrown = Assert.Throws<InvalidOperationException>(options.ToForwardedHeadersOptions);

        Assert.Contains("10.0.0.0/8", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trusted_proxy_may_set_the_client_address()
    {
        var context = await ThroughForwardedHeadersAsync(Proxy, Customer.ToString(), Proxy.ToString());

        Assert.Equal(Customer, context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task An_untrusted_peer_cannot_set_the_client_address()
    {
        var context = await ThroughForwardedHeadersAsync(
            Stranger,
            Customer.ToString(),
            Proxy.ToString());

        Assert.Equal(Stranger, context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task Each_customer_behind_a_trusted_proxy_gets_their_own_allowance()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ProxyFactory();
        using var client = factory.CreateClient();

        // The policy allows 30 writes a minute per partition.
        for (var i = 0; i < 30; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostStartAsync(client, FirstCustomer, ct));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await PostStartAsync(client, FirstCustomer, ct));

        // The reason for the change: another customer behind the same proxy has their own
        // budget. Were forwarded headers trusted after the limiter — or not at all — every
        // request would be keyed on the proxy's address and this would be refused too.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostStartAsync(client, SecondCustomer, ct));
    }

    private static async Task<HttpContext> ThroughForwardedHeadersAsync(
        IPAddress peer,
        string forwardedFor,
        params string[] trustedProxies)
    {
        var options = new TrustedProxyOptions { TrustedProxies = [.. trustedProxies] };

        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options.ToForwardedHeadersOptions()));

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer;
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;

        await middleware.Invoke(context);

        return context;
    }

    private static async Task<HttpStatusCode> PostStartAsync(
        HttpClient client,
        string forwardedFor,
        CancellationToken cancellationToken)
    {
        // An empty form: the limiter runs before antiforgery and validation, so an allowed
        // request is a 400 and a refused one is a 429. Nothing is created either way.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/RmaWizard/Start")
        {
            Content = new FormUrlEncodedContent([]),
        };

        request.Headers.Add("X-Forwarded-For", forwardedFor);

        using var response = await client.SendAsync(request, cancellationToken);

        return response.StatusCode;
    }

    /// <summary>
    /// Boots the Web host with a single trusted proxy and makes the test host look like a request
    /// from that proxy. TestServer reports no peer address, and an unknown peer is not trusted.
    /// </summary>
    private sealed class ProxyFactory : WebApplicationFactory<global::WebAppAnchor>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(
                "ConnectionStrings:Rma",
                "Host=localhost;Port=5432;Database=rmadev;Username=rmauser;Password=placeholder");

            builder.UseEnvironment("Testing");

            builder.UseSetting(
                $"{FakePaymentGatewayOptions.SectionName}:{nameof(FakePaymentGatewayOptions.AllowInProduction)}",
                "true");

            // Testing is outside Development, where /health's operator detail needs a key.
            builder.UseSetting(RagAdminGate.ApiKeyConfigurationKey, "proxy-tests-operator-key");

            builder.UseSetting($"{TrustedProxyOptions.SectionName}:TrustedProxies:0", Proxy.ToString());

            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(new PeerAddressStartupFilter(Proxy)));
        }
    }
}
