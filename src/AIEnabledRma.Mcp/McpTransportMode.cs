using Microsoft.Extensions.Configuration;

namespace AIEnabledRma.Mcp;

/// <summary>
/// Chooses the transport before any host is built: stdio for a developer's own machine, HTTP for
/// anything shared. Kept separate from both hosts so the choice itself is testable and neither
/// builder has to be constructed to find out which one is wanted.
/// </summary>
public static class McpTransportMode
{
    public const string ConfigurationKey = "Mcp:Transport";

    public const string Stdio = "stdio";

    public const string Http = "http";

    /// <summary>
    /// Reads the configured transport, treating anything unrecognised as a startup failure rather
    /// than falling back to stdio: a deployment that mistypes <c>http</c> would otherwise come up
    /// as a process that never listens, which looks healthy to a container runtime.
    /// </summary>
    public static string Read(string[] args)
    {
        // The same sources a host would read, in the same order, so `dotnet run -- --Mcp:Transport=http`,
        // appsettings.json and the environment variable all mean the same thing here as there.
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile("appsettings.json", optional: true);
        configuration.AddEnvironmentVariables();
        configuration.AddCommandLine(args);

        var value = configuration[ConfigurationKey];

        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), Stdio, StringComparison.OrdinalIgnoreCase))
        {
            return Stdio;
        }

        if (string.Equals(value.Trim(), Http, StringComparison.OrdinalIgnoreCase))
        {
            return Http;
        }

        throw new InvalidOperationException(
            $"{ConfigurationKey} is '{value}'. It must be '{Stdio}' or '{Http}'.");
    }
}
