using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIEnabledRma.Tests;

/// <summary>
/// The gate's negative cases are minted from a client the realm has to carry, with two audience
/// scopes and a one-second token lifespan. Without those the script's assertions would still pass
/// as written but stop proving anything, because the tokens it mints would be the same shape the
/// accepted one has.
/// </summary>
/// <remarks>
/// This is the realm-shaped half of "the gate fails closed": a token for another audience, the right
/// audience with no read scope, and a token that expires. Each case needs its own scope or lifespan,
/// and all three live in <c>infra/keycloak/realm-aienabledrma.json</c>, which is why they are
/// asserted here instead of being left to the script to discover at run time against a live Keycloak.
/// </remarks>
public sealed class RealmGateClientTests
{
    private const string ClientId = "mcp-negative";

    [Fact]
    public void The_realm_carries_the_client_the_gate_mints_bad_tokens_from()
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(RealmPath()));
        var client = Client(realm);

        Assert.True(client.GetProperty("serviceAccountsEnabled").GetBoolean(), "the gate needs a service account");
        Assert.True(client.TryGetProperty("secret", out var secret) && !string.IsNullOrWhiteSpace(secret.GetString()),
            "the gate mints with client_credentials, so the client needs a secret");
        Assert.False(client.GetProperty("publicClient").GetBoolean(), "a public client cannot use client_credentials");

        // One second is what makes the expiry case cost a minute instead of five: the transport allows
        // a minute of clock skew, so the stub waits 65 seconds and asserts the refusal.
        Assert.Equal("1", client.GetProperty("attributes").GetProperty("access.token.lifespan").GetString());

        var optional = client.GetProperty("optionalClientScopes").EnumerateArray().Select(s => s.GetString()).ToArray();

        Assert.Contains("rma:read", optional);
        Assert.Contains("audience-aienabledrma-mcp", optional);
        Assert.Contains("audience-wrong", optional);
    }

    [Fact]
    public void The_two_audience_scopes_add_different_audiences()
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(RealmPath()));

        Assert.Equal("aienabledrma-mcp", AudienceOf(Scope(realm, "audience-aienabledrma-mcp")));
        Assert.Equal("aienabledrma-not-mcp", AudienceOf(Scope(realm, "audience-wrong")));
    }

    /// <summary>
    /// The client is allow-listed on purpose: each refusal has to come from the token, and a client
    /// missing from the list would make all three cases the same 403.
    /// </summary>
    [Fact]
    public void The_transport_is_configured_to_accept_the_gate_client()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot(), "docker-compose.yml"));

        Assert.Matches(new Regex($@"Mcp__Http__AllowedClientIds__\d+:\s*{Regex.Escape(ClientId)}", RegexOptions.CultureInvariant), compose);
    }

    [Fact]
    public void The_gate_asserts_all_three_refusals()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "verify-auth.sh"));

        Assert.Contains("a token for another audience is refused", script, StringComparison.Ordinal);
        Assert.Contains("a token without rma:read is refused", script, StringComparison.Ordinal);
        Assert.Contains("the same token is refused once expired", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The imported shape is read back through the admin API on every run, not just by the tests that
    /// parse the file: what Keycloak produced is the fact the flows depend on, and the two can differ
    /// — a description too long for the column, a mapper that did not attach, a client that lost its
    /// service account.
    /// </summary>
    [Fact]
    public void The_gate_reads_the_imported_shape_back()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "verify-auth.sh"));

        Assert.Contains("admin/realms/", script, StringComparison.Ordinal);
        Assert.Contains("admin token from the master realm", script, StringComparison.Ordinal);

        // Both clients' settings, the two scope mappers and the demo user's credential.
        Assert.Contains("mcp-agent has a service account", script, StringComparison.Ordinal);
        Assert.Contains("mcp-browser requires S256", script, StringComparison.Ordinal);
        Assert.Contains("rma:read adds the transport's audience", script, StringComparison.Ordinal);
        Assert.Contains("basic adds the subject", script, StringComparison.Ordinal);
        Assert.Contains("browser-demo has a password", script, StringComparison.Ordinal);

        // A table that produced no rows used to be a run with no failures, so the count is checked
        // before the rows are.
        Assert.Contains("realm shape rows produced", script, StringComparison.Ordinal);
    }

    private static string AudienceOf(JsonElement scope) =>
        scope.GetProperty("protocolMappers")
            .EnumerateArray()
            .Single(mapper => mapper.GetProperty("protocolMapper").GetString() == "oidc-audience-mapper")
            .GetProperty("config")
            .GetProperty("included.custom.audience")
            .GetString()!;

    private static JsonElement Client(JsonDocument realm) =>
        realm.RootElement.GetProperty("clients")
            .EnumerateArray()
            .Single(client => client.GetProperty("clientId").GetString() == ClientId);

    private static JsonElement Scope(JsonDocument realm, string name) =>
        realm.RootElement.GetProperty("clientScopes")
            .EnumerateArray()
            .Single(scope => scope.GetProperty("name").GetString() == name);

    private static string RealmPath() =>
        Path.Combine(RepositoryRoot(), "infra", "keycloak", "realm-aienabledrma.json");

    /// <summary>
    /// Walks up from the test binary to the solution directory. The RAG project copies the corpus next
    /// to the binary, so stopping at the first directory with a knowledge-base would find a build
    /// artifact instead of the checkout.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !(Directory.Exists(Path.Combine(directory.FullName, "knowledge-base"))
                    && File.Exists(Path.Combine(directory.FullName, "AIEnabledRma.slnx"))))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }
}
