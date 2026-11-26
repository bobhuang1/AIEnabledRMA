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
    /// The scope that admits the one tool which does not mask, <c>reveal_customer_contact</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not part of <see cref="RequiredScope"/>: every caller of these tools reads
    /// masked data, and the ability to read one unmasked record is a support role rather than a
    /// wider version of the same permission. A client can only hold it by being granted the scope,
    /// and the token that carries it is what the audit row records, so the trail answers "which
    /// grant unmasked this customer" rather than "some agent did". Equal to
    /// <see cref="RequiredScope"/> — ignoring case and padding, which is how a paste goes wrong — is
    /// refused at startup, because that value would make the separation above a comment.
    /// </remarks>
    public string SupportScope { get; set; } = "rma:support";

    /// <summary>
    /// Origins allowed to call the transport from a browser. Empty means no browser client is
    /// accepted; a server-side agent sends no <c>Origin</c> header and is unaffected.
    /// </summary>
    /// <remarks>
    /// The same list becomes the CORS policy, so a listed page is admitted by the browser and by
    /// the check rather than only refused by the second one. Origins are compared exactly: a
    /// different port or a subdomain is a different page.
    /// </remarks>
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

        if (string.IsNullOrWhiteSpace(SupportScope))
        {
            // Fail closed loudly rather than quietly: a blank scope can never appear in a token,
            // so the unmask tool would be permanently refused and the code would look present and
            // working to everyone reading it.
            throw new InvalidOperationException(
                $"{SectionName}:SupportScope is required. It is the scope that admits "
                + "reveal_customer_contact; with it empty, no token could ever be granted it.");
        }

        if (string.Equals(
                SupportScope.Trim(),
                RequiredScope.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            // The one configuration here that widens access rather than breaking it. A deployment
            // that copies the read scope into the support scope starts cleanly, the read tools keep
            // working, and every caller who may read masked data may now also read one record
            // unmasked — the grant D3 keeps separate, quietly merged by a paste. Case and padding
            // are ignored because they are how a copy goes wrong, not a way to keep the grants
            // apart: the identity compares scope strings as written, so a differently-cased copy
            // would instead leave the unmask tool unreachable, which is the same mistake failing
            // the other way.
            throw new InvalidOperationException(
                $"{SectionName}:SupportScope is '{SupportScope}', which is RequiredScope. The "
                + "unmask tool is admitted by the support scope alone, so equal values make every "
                + "caller holding the read scope an unmasker. Give the support role its own scope.");
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
