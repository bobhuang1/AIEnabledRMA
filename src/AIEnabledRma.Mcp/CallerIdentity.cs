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
}

/// <inheritdoc />
public sealed class HttpContextCallerIdentity(IHttpContextAccessor accessor) : ICallerIdentity
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
}
