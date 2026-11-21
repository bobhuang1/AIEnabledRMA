using AIEnabledRma.Ai;
using AIEnabledRma.Rag;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AIEnabledRma.Tests;

/// <summary>
/// The Web host's <c>/health</c> detail sits behind <c>Rag:Admin:ApiKey</c>, and when that key is
/// absent the gate falls back to loopback.
/// </summary>
/// <remarks>
/// Loopback is a fine fallback for <c>dotnet run</c> and worthless behind a reverse proxy on the
/// same machine, where every caller looks local. The failure this pins down is therefore not a
/// wrong response but a host that starts: a keyless Production deployment would serve the corpus
/// size, the load-error count and the provider name to anyone who can reach the proxy, and nothing
/// in the response would look wrong. The RAG host already refuses that configuration; these tests
/// put the Web host on the same rule and check that a keyed host still starts.
/// </remarks>
public sealed class WebHostStartupTests
{
    private sealed class HostFactory(bool production, string? adminKey) : WebApplicationFactory<global::WebAppAnchor>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(production ? "Production" : "Testing");

            // The bundled accept-everything gateway refuses to register outside Development, and
            // the host registers it whether or not a test exercises it. Without this the boot would
            // fail for a different reason and the assertion below would pass by accident.
            builder.UseSetting(
                $"{FakePaymentGatewayOptions.SectionName}:{nameof(FakePaymentGatewayOptions.AllowInProduction)}",
                "true");

            if (adminKey is not null)
            {
                builder.UseSetting(RagAdminGate.ApiKeyConfigurationKey, adminKey);
            }
        }
    }

    [Fact]
    public void A_production_host_refuses_to_start_without_the_admin_key()
    {
        using var factory = new HostFactory(production: true, adminKey: null);

        // The host is built inside CreateClient, so this is where a startup guard surfaces.
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains(RagAdminGate.ApiKeyConfigurationKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_production_host_with_a_key_starts_and_hides_the_detail()
    {
        using var factory = new HostFactory(production: true, adminKey: "production-operator-key");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Configured is not the same as authenticated: the key is required from every caller once
        // it is set, so an anonymous request still gets one word. This is what stops the guard from
        // being "start with a key and publish anyway".
        Assert.Equal("{\"status\":\"ok\"}", body);
    }
}
