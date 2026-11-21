using System.Text.Json;

namespace AIEnabledRma.Tests;

/// <summary>
/// Pins the pieces of the browser sign-in flow that no other test would notice losing.
/// </summary>
/// <remarks>
/// The PKCE flow was proved against a running Keycloak, but that proof lives in a terminal and not in
/// the repository. The realm is a committed JSON file and the compose service is a committed YAML
/// file, so a later edit can delete the public client, add a secret to it, widen the redirect, or drop
/// it from the transport's allow-lists, and every test still passes while the runbook's browser path
/// has quietly stopped existing. These assertions are about the committed configuration, not about a
/// server: they say the flow is still defined and still admitted.
/// </remarks>
public sealed class KeycloakBrowserClientTests
{
    private const string BrowserClientId = "mcp-browser";
    private const string BrowserOrigin = "http://localhost:5173";

    [Fact]
    public void The_browser_client_in_the_committed_realm_is_public_and_pkce_only()
    {
        using var document = JsonDocument.Parse(RepositoryFile(
            "infra", "keycloak", "realm-aienabledrma.json"));

        var realm = document.RootElement;
        var client = Assert.Single(
            realm.GetProperty("clients").EnumerateArray(),
            c => c.GetProperty("clientId").GetString() == BrowserClientId);

        // A public client has nothing to leak; the committed realm is a development one, but a secret
        // here would mean the "public" half of the description is false.
        Assert.True(client.GetProperty("publicClient").GetBoolean());
        Assert.False(client.TryGetProperty("secret", out _));

        // PKCE with S256 is the only thing binding an authorization code to the page that asked for
        // it on a public client, so it is required rather than offered.
        Assert.Equal(
            "S256",
            client.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString());

        Assert.True(client.GetProperty("standardFlowEnabled").GetBoolean());
        Assert.False(client.GetProperty("implicitFlowEnabled").GetBoolean());
        Assert.False(client.GetProperty("directAccessGrantsEnabled").GetBoolean());
        // Absent is the Keycloak default (false), and the committed file relies on that default.
        Assert.False(
            client.TryGetProperty("serviceAccountsEnabled", out var serviceAccounts)
            && serviceAccounts.GetBoolean());

        // The redirect is the page's origin and nothing else: one entry, loopback, and a path glob
        // that cannot be pointed at another host. A second entry would be a second place a code can
        // be delivered.
        var redirects = client.GetProperty("redirectUris").EnumerateArray().ToList();

        Assert.True(
            redirects.Count == 1,
            $"the browser client accepts {redirects.Count} redirect URIs; it should accept only the "
            + "loopback page, because every entry is somewhere an authorization code can be sent.");

        Assert.Equal($"{BrowserOrigin}/*", redirects[0].GetString());

        Assert.True(
            Uri.TryCreate(redirects[0].GetString(), UriKind.Absolute, out var redirect) && redirect.IsLoopback,
            $"the browser client's redirect '{redirects[0]}' is not loopback.");

        // The browser posts the code exchange to Keycloak directly, so the origin has to be listed
        // for that request too; without it the token endpoint answers a preflight with nothing.
        var origins = client.GetProperty("webOrigins").EnumerateArray().ToList();

        Assert.All(
            origins,
            origin => Assert.True(
                Uri.TryCreate(origin.GetString(), UriKind.Absolute, out var uri) && uri.IsLoopback,
                $"the browser client lists the origin '{origin}', which is not loopback."));

        // The token is judged by the transport for rma:read, so the browser client must be granted
        // it or the flow succeeds and every tool call is refused.
        Assert.Contains(
            "rma:read",
            client.GetProperty("defaultClientScopes").EnumerateArray().Select(s => s.GetString()!));
    }

    [Fact]
    public void The_compose_service_allow_lists_the_browser_client_and_its_origin()
    {
        var compose = RepositoryFile("docker-compose.yml");
        var service = ComposeService(compose, "mcp-http");

        // Both allow-lists are what make the browser flow usable: client ids decide which tokens the
        // transport accepts, origins decide which pages may send one. A client listed without its
        // origin (or the reverse) documents a flow nobody can run.
        Assert.Contains(
            $"Mcp__Http__AllowedClientIds__1: {BrowserClientId}",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Mcp__Http__AllowedOrigins__0: {BrowserOrigin}",
            service,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// One service's block from the compose file, from its own key to the next line at the same
    /// indentation. Enough YAML for an assertion about two environment entries, without adding a YAML
    /// dependency for it.
    /// </summary>
    private static string ComposeService(string compose, string serviceName)
    {
        var lines = compose.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, line => line == $"  {serviceName}:");

        Assert.True(start >= 0, $"docker-compose.yml has no '{serviceName}:' service.");

        var end = start + 1;
        while (end < lines.Length && (lines[end].Length == 0 || lines[end].StartsWith("   ", StringComparison.Ordinal)))
        {
            end++;
        }

        return string.Join('\n', lines[start..end]);
    }

    /// <summary>
    /// A committed file, found by walking up from the test binary: the test host's working directory
    /// is the build output, and the files these tests pin are repository content rather than build
    /// output.
    /// </summary>
    private static string RepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeParts]);

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find {Path.Combine(relativeParts)} above {AppContext.BaseDirectory}.");
    }
}
