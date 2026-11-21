using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace AIEnabledRma.Mcp;

/// <summary>
/// The RFC 9728 document, as this transport publishes it.
/// </summary>
/// <remarks>
/// Field names are fixed by the RFC, so they are annotated rather than relying on a serializer's
/// default casing: <c>authorization_servers</c> is not a camelCase of anything.
/// </remarks>
public sealed record ProtectedResourceMetadataDocument
{
    /// <summary>The protected resource's identifier: the exact URL a client connects to.</summary>
    [JsonPropertyName("resource")]
    public required string Resource { get; init; }

    /// <summary>Issuers this resource trusts. At least one, and MCP requires the field.</summary>
    [JsonPropertyName("authorization_servers")]
    public required IReadOnlyList<string> AuthorizationServers { get; init; }

    [JsonPropertyName("scopes_supported")]
    public required IReadOnlyList<string> ScopesSupported { get; init; }

    /// <summary>How a caller may present the token. Only the header is accepted here.</summary>
    [JsonPropertyName("bearer_methods_supported")]
    public required IReadOnlyList<string> BearerMethodsSupported { get; init; }

    [JsonPropertyName("resource_name")]
    public required string ResourceName { get; init; }
}

/// <summary>
/// Discovery for the remote transport: where the document lives, and the challenge that points at it.
/// </summary>
/// <remarks>
/// Without this, a client holding a URL had to be configured with the realm by hand, which is exactly
/// what every command in <c>docs/mcp.md</c> used to do. The document has to be reachable without a
/// token — it describes how to obtain one — and the URL in the challenge is absolute, built from the
/// request, because the host a client reached is the host it can follow.
/// </remarks>
public static class ProtectedResourceMetadata
{
    /// <summary>The registered well-known URI for this document (RFC 9728 §3).</summary>
    public const string WellKnownPath = "/.well-known/oauth-protected-resource";

    /// <summary>
    /// The endpoint-specific path. RFC 9728 inserts the well-known segment between the host and the
    /// resource's path, so a resource at <c>/mcp</c> publishes at
    /// <c>/.well-known/oauth-protected-resource/mcp</c> rather than at the host root.
    /// </summary>
    public static string PathFor(string endpointPath)
    {
        ArgumentNullException.ThrowIfNull(endpointPath);

        return endpointPath is "" or "/"
            ? WellKnownPath
            : WellKnownPath + endpointPath;
    }

    /// <summary>The absolute URL the challenge names, as seen by the host the caller reached.</summary>
    private static string UrlFor(HttpRequest request, McpHttpOptions http)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);

        return $"{request.Scheme}://{request.Host}{PathFor(http.Path)}";
    }

    public static ProtectedResourceMetadataDocument DocumentFor(HttpRequest request, McpHttpOptions http)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);

        return new ProtectedResourceMetadataDocument
        {
            // The resource identifier is the endpoint URL, not the well-known URL: a client compares
            // it with the value it would have used, so a mismatch is a refusal (RFC 9728 §3.3).
            Resource = $"{request.Scheme}://{request.Host}{http.Path}",
            AuthorizationServers = [http.Issuer],
            ScopesSupported = [http.RequiredScope],
            BearerMethodsSupported = ["header"],
            ResourceName = "AIEnabledRma MCP tools",
        };
    }

    /// <summary>
    /// The <c>WWW-Authenticate</c> value for a 401. The scope is named as well as the metadata URL, so
    /// a client asks its authorization server for the scope this resource actually requires.
    /// </summary>
    public static string Challenge(HttpRequest request, McpHttpOptions http) =>
        $"Bearer resource_metadata=\"{UrlFor(request, http)}\", scope=\"{http.RequiredScope}\"";
}
