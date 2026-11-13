using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AIEnabledRma.Rag;

/// <summary>
/// Gate for the RAG host's operational endpoints: <c>/diagnostics</c>, <c>/retrieve</c> and
/// <c>/rag/reload</c>.
/// </summary>
/// <remarks>
/// None of these are customer surfaces. <c>/diagnostics</c> lists the corpus and the directory
/// it was read from, <c>/retrieve</c> is a real ranked search over the same corpus, and
/// <c>/rag/reload</c> is a state change that rebuilds the index from disk. Left open, the host
/// is a corpus leak and a free search API to anyone who can reach the port, and the reload
/// endpoint is an unauthenticated write.
///
/// Two ways in:
/// <list type="number">
/// <item>A key in <c>Rag:Admin:ApiKey</c>, presented in the <c>X-Rag-Admin-Key</c> header and
/// compared in fixed time, so a wrong key cannot be recovered one byte at a time from response
/// timings.</item>
/// <item>Loopback, when no key is configured. That keeps <c>dotnet run</c> on a developer's own
/// machine usable without inventing a secret, and grants nothing to anyone else.</item>
/// </list>
///
/// When a key is configured it is required from every caller, loopback included: one rule with
/// no ambiguity about whether the host is "in key mode" for a given request.
///
/// A refusal is a 404 rather than a 401 or 403. The endpoints are not advertised, so a caller
/// who was not told they exist should not learn that they do.
/// </remarks>
public static class RagAdminGate
{
    /// <summary>The header that carries the admin key.</summary>
    public const string HeaderName = "X-Rag-Admin-Key";

    /// <summary>Configuration key that holds the admin key. Empty means loopback only.</summary>
    public const string ApiKeyConfigurationKey = "Rag:Admin:ApiKey";

    /// <summary>True when this request may use the operational endpoints.</summary>
    public static bool IsAllowed(HttpContext context, string? configuredKey)
    {
        if (string.IsNullOrEmpty(configuredKey))
        {
            // IsLoopback is a static helper on IPAddress, not an instance property. It also
            // unwraps IPv4-mapped IPv6 addresses, which is what a loopback client on a
            // dual-stack socket looks like.
            var remote = context.Connection.RemoteIpAddress;

            return remote is not null && IPAddress.IsLoopback(remote);
        }

        var supplied = context.Request.Headers[HeaderName].ToString();
        if (supplied.Length == 0)
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(configuredKey);
        var actual = Encoding.UTF8.GetBytes(supplied);

        // Length is compared first and then the bytes, because FixedTimeEquals throws on a
        // length mismatch rather than returning false.
        return actual.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
