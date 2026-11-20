using System.Security.Claims;

namespace AIEnabledRma.Mcp.Security;

/// <summary>Who is calling a tool, as far as the server can tell.</summary>
public sealed record McpCaller(string Id, bool IsAuthenticated, IReadOnlySet<string> Permissions)
{
    public bool Has(string permission) => Permissions.Contains(permission);

    /// <summary>
    /// Builds the caller from the transport's principal. An authenticated principal (HTTP with a
    /// validated token) is identified by its client id, falling back to its subject. Without
    /// one the configured local caller name is used and no permission is granted, so a stdio
    /// caller can never reach a privileged tool.
    /// </summary>
    public static McpCaller From(ClaimsPrincipal? user, McpSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (user?.Identity?.IsAuthenticated != true)
        {
            return new McpCaller(options.LocalCallerName, false, new HashSet<string>());
        }

        var id = user.FindFirst("client_id")?.Value
            ?? user.FindFirst("azp")?.Value
            ?? user.FindFirst("appid")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? "authenticated-unknown";

        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in user.Claims)
        {
            switch (claim.Type)
            {
                case "scope":
                case "scp":
                case "http://schemas.microsoft.com/identity/claims/scope":
                    foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        permissions.Add(scope);
                    }

                    break;
                case "roles":
                case "role":
                case ClaimTypes.Role:
                    permissions.Add(claim.Value);
                    break;
            }
        }

        return new McpCaller(id, true, permissions);
    }
}
