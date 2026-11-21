using System.Net;
using System.Security.Cryptography;
using System.Text;
using AIEnabledRma.Domain.Configuration;

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
/// The loopback fallback is a development convenience, so a keyless host refuses to start
/// outside Development (<see cref="EnsureStartupAllowed"/>): a proxy on the same machine makes
/// every caller look like loopback, which would leave all three endpoints open. A configured but
/// placeholder key is refused for the same reason: the example value is published in this
/// repository, so it admits every reader.
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

    /// <summary>
    /// Throws unless this configuration may serve the operational endpoints. A key is mandatory
    /// outside Development, where the loopback fallback would admit a proxy on the same machine.
    /// </summary>
    public static void EnsureStartupAllowed(string? configuredKey, bool isDevelopment)
    {
        if (!isDevelopment && ProductionSecrets.IsPlaceholder(configuredKey))
        {
            // Present but not a secret: the example env file's `replace-with-a-long-random-key`
            // satisfies a non-empty check and publishes the operational endpoints to anyone who
            // has read this repository.
            throw new InvalidOperationException(ProductionSecrets.Problem(ApiKeyConfigurationKey));
        }

        if (!string.IsNullOrEmpty(configuredKey) || isDevelopment)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{ApiKeyConfigurationKey} is required outside Development. Without it the operator "
            + "endpoints admit any loopback caller, and a reverse proxy on the same host makes "
            + "every caller loopback.");
    }

    /// <summary>
    /// Which credential admitted this request, for the record a state change writes.
    /// </summary>
    /// <remarks>
    /// Deliberately not an identity: the key is shared, so all it can honestly say is that the
    /// admin key was presented or that no key is configured and the caller is local. The address
    /// travels beside it in <see cref="RagReloadAttempt.RemoteAddress"/> for the same reason.
    /// </remarks>
    public static string DescribeCaller(string? configuredKey) =>
        string.IsNullOrEmpty(configuredKey) ? "loopback" : "admin-key";

    /// <summary>True when this request may use the operational endpoints.</summary>
    public static bool IsAllowed(HttpContext context, string? configuredKey)
    {
        if (string.IsNullOrEmpty(configuredKey))
        {
            // A dual-stack socket presents a loopback client as an IPv4-mapped IPv6 address,
            // which IsLoopback recognises; a request with no address is not assumed local.
            var remote = context.Connection.RemoteIpAddress;
            return remote is not null && IPAddress.IsLoopback(remote);
        }

        var supplied = context.Request.Headers[HeaderName].ToString();
        var expected = Encoding.UTF8.GetBytes(configuredKey);
        var actual = Encoding.UTF8.GetBytes(supplied);

        // Length is compared first and then the bytes, because FixedTimeEquals throws on a
        // length mismatch rather than returning false.
        return actual.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
