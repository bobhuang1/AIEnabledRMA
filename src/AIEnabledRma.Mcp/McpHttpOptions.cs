namespace AIEnabledRma.Mcp;

/// <summary>
/// Settings for the remote MCP transport, bound from <c>Mcp:Http</c>.
/// </summary>
/// <remarks>
/// The defaults are the local Keycloak realm the compose file describes: a developer runs Keycloak,
/// creates the realm, and the host validates against it without further configuration. Nothing here
/// is a secret — a client secret belongs to the agent, not the server — so a committed default costs
/// nothing and a deployment only overrides what it must.
/// </remarks>
public sealed class McpHttpOptions
{
    public const string SectionName = "Mcp:Http";

    /// <summary>
    /// OIDC issuer, and the value the token's <c>iss</c> must equal. Keycloak's shape is
    /// <c>{base}/realms/{realm}</c>, so the default is the local realm.
    /// </summary>
    public string Issuer { get; set; } = "http://localhost:8080/realms/aienabledrma";

    /// <summary>
    /// Audience the access token must be minted for. Keycloak does not add this by default: the
    /// realm needs an audience mapper on the client scope that puts <c>aienabledrma-mcp</c> into
    /// <c>aud</c>, and this check is what makes forgetting that mapper a startup-time 401 rather
    /// than an agent that reaches nothing.
    /// </summary>
    public string Audience { get; set; } = "aienabledrma-mcp";

    /// <summary>The scope an agent must hold to call any tool.</summary>
    public string RequiredScope { get; set; } = "rma:read";

    /// <summary>
    /// Origins allowed to call the transport from a browser. Empty means no browser client is
    /// accepted; a server-side agent sends no <c>Origin</c> header and is unaffected.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Registered clients allowed to use the transport, by <c>client_id</c>. An empty list is
    /// refused at startup: without it any client in the realm that can obtain a token for this
    /// audience is an MCP client.
    /// </summary>
    public string[] AllowedClientIds { get; set; } = [];

    /// <summary>Path the MCP endpoint is mapped at.</summary>
    public string Path { get; set; } = "/mcp";

    /// <summary>
    /// Throws when the transport cannot be served safely. Called at startup, so a deployment that
    /// forgot the issuer or the allow-list fails where someone notices, rather than serving tools
    /// to whatever can reach the port.
    /// </summary>
    public void EnsureUsable(bool isDevelopment)
    {
        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Issuer must be an absolute URL, and it is '{Issuer}'. It is the "
                + "Keycloak realm, e.g. http://localhost:8080/realms/aienabledrma.");
        }

        if (!isDevelopment && issuer.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Issuer is '{Issuer}'. Outside Development the issuer must be https: "
                + "a token fetched over plain http can be replaced in flight, which makes every "
                + "check below meaningless.");
        }

        if (string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Audience is required. Without it a token minted for any other "
                + "service in the realm would be accepted here.");
        }

        if (string.IsNullOrWhiteSpace(RequiredScope))
        {
            throw new InvalidOperationException(
                $"{SectionName}:RequiredScope is required. Without it any authenticated token, "
                + "including one issued for an unrelated client, could call the tools.");
        }

        if (AllowedClientIds.Length == 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:AllowedClientIds is empty. List the clients allowed to call these "
                + "tools; an empty list would make every client in the realm an MCP client.");
        }

        if (AllowedOrigins.Any(o => o is "*" or ""))
        {
            throw new InvalidOperationException(
                $"{SectionName}:AllowedOrigins cannot contain '*' or an empty entry. The list is "
                + "an allow-list; a wildcard turns the origin check into decoration.");
        }

        if (!Path.StartsWith('/'))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Path must start with '/', and it is '{Path}'.");
        }
    }
}
