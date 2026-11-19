using System.Net;
using System.Security.Cryptography;
using System.Text;
using AIEnabledRma.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AIEnabledRma.Tests;

/// <summary>
/// A refetch has to reach the identity provider and take what it now serves, not what the transport
/// cached before the realm was re-imported.
/// </summary>
/// <remarks>
/// This is the test for the second bug in this area, and the one that the first hand-run of the fix
/// exposed: marking the configuration cache stale and reading it back returned the previous document,
/// because the cache's own refresh interval had not elapsed. The transport then validated with the
/// replaced key and answered 401 to a fresh token. A provider serving a different key id on demand is
/// what distinguishes a real refetch from a cache read, and the assertions are written so a return to
/// <c>RequestRefresh</c> fails here instead of in a container.
/// </remarks>
public sealed class SigningKeySetTests
{
    [Fact]
    public async Task A_refetch_returns_the_key_the_provider_now_serves()
    {
        await using var provider = await JwksProvider.StartAsync("key-a");

        var keys = new ConfigurationSigningKeySet(() => new ConfigurationManager<OpenIdConnectConfiguration>(
            provider.MetadataUrl,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = false }));

        Assert.Equal(["key-a"], keys.Current.Select(key => key.KeyId).Distinct().ToArray());

        provider.Serve("key-b");

        var refetched = keys.Refetch();

        Assert.Equal(["key-b"], refetched.Select(key => key.KeyId).Distinct().ToArray());

        // The next read is the refetched set too, so a request that follows does not go back to the
        // key the provider has retired.
        Assert.Equal(["key-b"], keys.Current.Select(key => key.KeyId).Distinct().ToArray());
    }

    /// <summary>
    /// A discovery document and a key set over loopback, with the key id switchable so one test can
    /// stand in for a realm being re-imported.
    /// </summary>
    private sealed class JwksProvider : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly RSA rsa = RSA.Create(2048);
        private volatile string keyId;

        private JwksProvider(WebApplication app, string keyId)
        {
            this.app = app;
            this.keyId = keyId;
        }

        public string MetadataUrl { get; private set; } = string.Empty;

        public static async Task<JwksProvider> StartAsync(string keyId)
        {
            var builder = WebApplication.CreateSlimBuilder();

            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var app = builder.Build();
            var provider = new JwksProvider(app, keyId);

            app.MapGet("/realm/.well-known/openid-configuration", (HttpContext context) =>
            {
                var origin = $"{context.Request.Scheme}://{context.Request.Host}";

                return Results.Json(new
                {
                    issuer = origin + "/realm",
                    jwks_uri = origin + "/realm/jwks",
                    id_token_signing_alg_values_supported = new[] { "RS256" },
                    response_types_supported = new[] { "code" },
                    subject_types_supported = new[] { "public" },
                });
            });

            app.MapGet("/realm/jwks", () => Results.Text(provider.KeySetJson(), "application/json"));

            await app.StartAsync(TestContext.Current.CancellationToken);

            provider.MetadataUrl = $"{app.Urls.First()}/realm/.well-known/openid-configuration";

            return provider;
        }

        public void Serve(string keyId) => this.keyId = keyId;

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            rsa.Dispose();
        }

        private string KeySetJson()
        {
            var parameters = rsa.ExportParameters(includePrivateParameters: false);

            return new StringBuilder()
                .Append("{\"keys\":[{")
                .Append("\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",")
                .Append($"\"kid\":\"{keyId}\",")
                .Append($"\"n\":\"{Base64UrlEncoder.Encode(parameters.Modulus)}\",")
                .Append($"\"e\":\"{Base64UrlEncoder.Encode(parameters.Exponent)}\"")
                .Append("}]}")
                .ToString();
        }
    }
}
