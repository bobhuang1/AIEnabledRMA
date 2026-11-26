using Microsoft.AspNetCore.Http;

namespace AIEnabledRma.Mcp;

/// <summary>
/// Who a tool call belongs to, as the audit row records it.
/// </summary>
/// <remarks>
/// On stdio this is one named local caller and always will be: there is no identity provider in
/// the process. On HTTP the token has already been validated by the time a tool runs, so the
/// client and subject it carries are the honest answer, and an audit trail whose every row says
/// "local" would not be one.
/// </remarks>
public interface ICallerIdentity
{
    string Describe();

    /// <summary>
    /// Whether this caller holds the grant that admits <c>reveal_customer_contact</c>.
    /// </summary>
    /// <remarks>
    /// Asked of the same object as <see cref="Describe"/> on purpose: the audit row and the decision
    /// to unmask must be about one caller, so a gate that checked a different claim than the one the
    /// row records would let the trail name someone who was never the reason.
    /// </remarks>
    bool IsSupportCaller { get; }
}

/// <inheritdoc />
/// <remarks>
/// The support scope is handed in rather than read from options: only the HTTP host has an identity
/// provider to read it from, and a null scope means no token can be a support caller — which is the
/// honest answer on stdio, where <see cref="Describe"/> already says there is nobody to ask.
/// </remarks>
public sealed class HttpContextCallerIdentity(IHttpContextAccessor accessor, string? supportScope)
    : ICallerIdentity
{
    public string Describe()
    {
        var user = accessor.HttpContext?.User;

        if (user?.Identity?.IsAuthenticated != true)
        {
            // No request, or a request that never authenticated: stdio, or a change that removed
            // the authorization policy. Either way the row still says which of the two.
            return ToolCallGuard.CallerName;
        }

        var client = user.FindFirst("client_id")?.Value
            ?? user.FindFirst("azp")?.Value
            ?? "unknown-client";

        var subject = user.FindFirst("sub")?.Value ?? "unknown-subject";

        return $"{client}:{subject}";
    }

    /// <summary>
    /// The token's own grant, read the way the transport reads the read scope: <c>scope</c> or
    /// <c>scp</c>, space-separated, compared exactly.
    /// </summary>
    public bool IsSupportCaller
    {
        get
        {
            var user = accessor.HttpContext?.User;

            if (string.IsNullOrWhiteSpace(supportScope) || user?.Identity?.IsAuthenticated != true)
            {
                return false;
            }

            return user.Claims
                .Where(claim => claim.Type is "scope" or "scp")
                .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Contains(supportScope, StringComparer.Ordinal);
        }
    }
}
