namespace AIEnabledRma.Tests;

/// <summary>
/// Shared rules for the suites that need a real PostgreSQL server.
/// </summary>
internal static class LiveDatabase
{
    /// <summary>Admin connection string for the server that hosts the throwaway databases.</summary>
    public const string ConnectionVariable = "RMA_TEST_CONNECTION";

    /// <summary>
    /// When set to <c>1</c> or <c>true</c>, an unreachable server fails the test instead of skipping
    /// it. CI sets this, so a green run can never quietly contain zero database tests.
    /// </summary>
    public const string RequireVariable = "RMA_REQUIRE_LIVE_DB";

    /// <summary>
    /// CREATE DATABASE and DROP DATABASE ... WITH (FORCE) checkpoint the whole cluster. On a busy
    /// Docker Desktop volume that has been measured at two minutes, far beyond Npgsql's 30-second
    /// default, so these statements get a generous timeout of their own.
    /// </summary>
    public const int DdlTimeoutSeconds = 600;

    public static bool IsRequired
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(RequireVariable);
            return value is not null
                && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Skips the test, or fails it when the live database is required.</summary>
    public static void Unavailable(Exception ex)
    {
        var message = $"PostgreSQL is not reachable: {ex.GetType().Name}: {ex.Message}";

        if (IsRequired)
        {
            Assert.Fail($"{message} ({RequireVariable} is set, so this is a failure, not a skip.)");
        }

        Assert.Skip(message);
    }
}
