namespace AIEnabledRma.Domain.Configuration;

/// <summary>
/// The database passwords this repository ships in its development and CI settings, and the problem
/// text a Production host reports when it finds one.
/// </summary>
/// <remarks>
/// The rule lives in this assembly because every host that opens the database already references it:
/// the web app, both tool-server transports and the RAG host's pgvector store. Hosts that kept their
/// own copy drifted from the others, and a host with no copy at all started cleanly on a password
/// that cannot authenticate, failing at its first query instead of at startup.
/// </remarks>
public static class ProductionDatabaseCredentials
{
    /// <summary>The problem a host reports when the configured password is one of these.</summary>
    public const string DefaultPasswordProblem = "ConnectionStrings:Rma uses a default or empty password.";

    /// <summary>The problem a host reports when the connection string cannot be read at all.</summary>
    public const string MalformedConnectionProblem = "ConnectionStrings:Rma is not a valid PostgreSQL connection string.";

    // Real passwords that appear in this repository's development and CI settings. The checked-in
    // placeholder is not one: it never authenticates, so a host left on it fails at its first query,
    // and the owner's Production boot tests start on it deliberately.
    private static readonly HashSet<string> KnownDefaults = new(StringComparer.Ordinal)
    {
        "rmapassword", "postgres", "password", "ci-only-password", "testonly", "changeme",
    };

    /// <summary>Whether the password is one this repository uses for development or CI.</summary>
    public static bool IsKnownDefault(string? password) =>
        !string.IsNullOrEmpty(password) && KnownDefaults.Contains(password);
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
