using System.Security.Cryptography;
using System.Text;

namespace AIEnabledRma.Rag;

/// <summary>
/// API keys for the RAG host's operator endpoints (F2). Supplied from the environment or a
/// secret store (<c>Rag__Security__AdminKey</c>), never from a checked-in file.
/// </summary>
public sealed class RagSecurityOptions
{
    public const string SectionName = "Rag:Security";

    /// <summary>Unlocks <c>/rag/reload</c> and the full <c>/diagnostics</c>.</summary>
    public string? AdminKey { get; set; }

    /// <summary>Unlocks <c>/retrieve</c>. The admin key works too.</summary>
    public string? ReaderKey { get; set; }
}

public enum RagAccess
{
    None,
    Reader,
    Admin,
}

/// <summary>Resolves what the caller's <c>X-Api-Key</c> header grants.</summary>
public static class RagApiKeys
{
    public const string HeaderName = "X-Api-Key";

    /// <summary>
    /// Admin when the header matches the admin key, Reader when it matches the reader key.
    /// In Development with no keys configured at all, every caller is Admin so the host stays
    /// usable on a laptop; anywhere else an unconfigured key grants nothing.
    /// </summary>
    public static RagAccess Resolve(HttpContext context, RagSecurityOptions options, IHostEnvironment environment)
    {
        if (string.IsNullOrEmpty(options.AdminKey) && string.IsNullOrEmpty(options.ReaderKey))
        {
            return environment.IsDevelopment() ? RagAccess.Admin : RagAccess.None;
        }

        var supplied = context.Request.Headers[HeaderName].ToString();
        if (supplied.Length == 0)
        {
            return RagAccess.None;
        }

        if (Matches(supplied, options.AdminKey))
        {
            return RagAccess.Admin;
        }

        return Matches(supplied, options.ReaderKey) ? RagAccess.Reader : RagAccess.None;
    }

    private static bool Matches(string supplied, string? expected) =>
        !string.IsNullOrEmpty(expected)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
}
