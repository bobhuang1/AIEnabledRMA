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
    internal static string RepositoryRoot()
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

/// <summary>
/// The unmask tool's grant is a realm object, and no change in the tool server can substitute for
/// it: the tool asks whether the token carries the scope, so a realm that never grants it leaves the
/// tool refused for every caller while every other check still passes.
/// </summary>
/// <remarks>
/// The machine client may request the scope and does not hold it by default, which is the shape the
/// grant has to have: the ordinary agent token still reads masked data, and unmasking is a grant
/// somebody asked for. The gate mints that token, because the other evidence is a refusal that reads
/// like a tool answer — a sentence the agent could mistake for the tool working.
/// </remarks>
public sealed class RealmSupportScopeTests
{
    private const string ScopeName = "rma:support";

    private static JsonElement Realm(JsonDocument realm) => realm.RootElement;

    [Fact]
    public void The_realm_defines_the_support_scope_as_a_token_scope()
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(RealmPath()));

        var scope = Realm(realm).GetProperty("clientScopes")
            .EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == ScopeName);

        // include.in.token.scope is what puts the name in the token's scope claim; without it the
        // scope exists in the realm and no token ever shows it to the transport.
        Assert.Equal("true", scope.GetProperty("attributes").GetProperty("include.in.token.scope").GetString());

        // Keycloak stores a client scope description in VARCHAR(255) and refuses the whole import
        // otherwise, which is a stack that will not start rather than a bad description.
        Assert.True(
            scope.GetProperty("description").GetString()!.Length <= 255,
            "the description has to fit Keycloak's column, or the realm will not import");
    }

    [Fact]
    public void The_agent_client_may_request_the_support_scope_but_does_not_hold_it_by_default()
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(RealmPath()));

        var agent = Realm(realm).GetProperty("clients")
            .EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == "mcp-agent");

        var optional = agent.GetProperty("optionalClientScopes").EnumerateArray()
            .Select(s => s.GetString())
            .ToArray();
        var defaults = agent.GetProperty("defaultClientScopes").EnumerateArray()
            .Select(s => s.GetString())
            .ToArray();

        // Requestable, so the gate can mint a support token from the committed realm...
        Assert.Contains(ScopeName, optional);

        // ...and not a default, so the everyday machine token cannot unmask by accident.
        Assert.DoesNotContain(ScopeName, defaults);
    }

    [Fact]
    public void The_gate_mints_the_support_token_and_asserts_its_scope()
    {
        var script = File.ReadAllText(Path.Combine(RealmGateClientTests.RepositoryRoot(), "scripts", "verify-auth.sh"));

        Assert.Contains("-d 'scope=rma:read rma:support'", script, StringComparison.Ordinal);
        Assert.Contains("support token carries rma:support", script, StringComparison.Ordinal);
        Assert.Contains("mcp-agent may request rma:support", script, StringComparison.Ordinal);
    }

    private static string RealmPath() =>
        Path.Combine(RealmGateClientTests.RepositoryRoot(), "infra", "keycloak", "realm-aienabledrma.json");
}
