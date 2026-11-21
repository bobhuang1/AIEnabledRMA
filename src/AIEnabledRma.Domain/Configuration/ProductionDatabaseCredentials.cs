using System.Data.Common;

namespace AIEnabledRma.Domain.Configuration;

/// <summary>
/// The database passwords this repository ships in its development, CI and example settings, and the
/// problem text a Production host reports when it finds one.
/// </summary>
/// <remarks>
/// The rule lives in this assembly because every host that opens the database already references it:
/// the web app, both tool-server transports and the RAG host's pgvector store. Hosts that kept their
/// own copy drifted from the others, and a host with no copy at all started cleanly on a password
/// that cannot authenticate, failing at its first query instead of at startup.
///
/// Two kinds of value are refused, and they fail for different reasons. A <em>development default</em>
/// (<c>rmapassword</c>) is a real password that authenticates against the database this repository's
/// compose file starts, so a Production host left on it is reachable by anyone who read the
/// repository. A <em>placeholder</em> (<c>replace-with-a-long-random-password</c>) never
/// authenticates at all, so the host starts, claims to be configured, and fails its first query —
/// which reads as an outage rather than as the misconfiguration it is.
///
/// The parsing uses <see cref="DbConnectionStringBuilder"/> rather than Npgsql's builder: the rule
/// only needs the <c>Password</c> keyword, and a generic parser keeps this assembly free of a
/// database client. Npgsql accepts the same key/value syntax, and a string this cannot read is one
/// Npgsql cannot read either.
/// </remarks>
public static class ProductionDatabaseCredentials
{
    /// <summary>The problem a host reports when the configured password is one of these.</summary>
    public const string DefaultPasswordProblem = "ConnectionStrings:Rma uses a default, placeholder or empty password.";

    /// <summary>The problem a host reports when the connection string cannot be read at all.</summary>
    public const string MalformedConnectionProblem = "ConnectionStrings:Rma is not a valid PostgreSQL connection string.";

    // Real passwords that appear in this repository's development and CI settings.
    private static readonly HashSet<string> KnownDefaults = new(StringComparer.Ordinal)
    {
        "rmapassword", "postgres", "password", "ci-only-password", "testonly", "changeme",
    };

    /// <summary>
    /// Whether the password is one this repository uses for development or CI, a placeholder from
    /// the example settings, or empty.
    /// </summary>
    /// <remarks>
    /// Empty is included because the message has always said so. The earlier version excluded it to
    /// keep a test host on the checked-in placeholder starting, which left a Production deployment
    /// with no password booting cleanly and failing at its first query.
    /// </remarks>
    public static bool IsKnownDefault(string? password) =>
        string.IsNullOrWhiteSpace(password)
        || KnownDefaults.Contains(password!)
        || ProductionSecrets.IsPlaceholder(password);

    /// <summary>
    /// The problem with this connection string, or null when it is usable. A blank connection string
    /// returns null: whether a database is required is the host's decision, because the RAG host is
    /// valid without one.
    /// </summary>
    public static string? ProblemWith(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        string? password;

        try
        {
            var parsed = new DbConnectionStringBuilder { ConnectionString = connectionString };
            password = parsed.TryGetValue("Password", out var value) ? value as string : null;
        }
        catch (ArgumentException)
        {
            return MalformedConnectionProblem;
        }

        return IsKnownDefault(password) ? DefaultPasswordProblem : null;
    }
}

/// <summary>
/// The refusal every Production host throws, so an operator reads the same sentence whichever host
/// stopped and the tests can assert it once.
/// </summary>
public static class ProductionConfigurationGuard
{
    public static InvalidOperationException Refusal(params string[] problems) =>
        new("Refusing to start in Production with an unsafe configuration:\n  - "
            + string.Join("\n  - ", problems));
}
