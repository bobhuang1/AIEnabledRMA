namespace AIEnabledRma.Tests;

/// <summary>
/// The development compose file and the recipe for the local embedding model.
/// </summary>
/// <remarks>
/// D4 makes Ollama the default embedding provider, and the server that answers for it is not part of
/// <c>docker compose up</c>. Without an entry in the file, a fresh clone has a configured provider that
/// nothing serves, and hybrid retrieval answers from the lexical index with no server to start. The
/// profile keeps the default `up` as small as it was while making the missing piece one command away,
/// and the pull is asserted because a server with the model absent fails the same way an absent server
/// does.
/// </remarks>
public sealed class DevelopmentComposeTests
{
    [Fact]
    public void The_local_embedding_server_is_a_profile_on_the_default_url_with_a_kept_volume()
    {
        var service = ComposeService(RepositoryFile("docker-compose.yml"), "ollama");

        Assert.Contains("profiles: [\"ollama\"]", service, StringComparison.Ordinal);
        Assert.Contains("image: ollama/ollama:", service, StringComparison.Ordinal);

        // The default Embeddings:BaseUrl points here, so the profile has to publish the loopback port.
        Assert.Contains("127.0.0.1:${OLLAMA_PORT:-11434}:11434", service, StringComparison.Ordinal);
        Assert.Contains("ollama:/root/.ollama", service, StringComparison.Ordinal);
        // The model lives in a named volume, so a restart or `compose down` does not re-download it.
        Assert.Matches(
            "(?m)^volumes:\\n(?:  #.*\\n)*  ollama:",
            RepositoryFile("docker-compose.yml").ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_model_pull_is_documented_for_the_profile()
    {
        var readme = RepositoryFile("README.md");

        Assert.Contains("docker compose --profile ollama up -d ollama", readme, StringComparison.Ordinal);
        Assert.Contains("docker compose exec ollama ollama pull nomic-embed-text", readme, StringComparison.Ordinal);
    }

    /// <summary>
    /// One service's block from the compose file, from its own key to the next line at the same
    /// indentation. Enough YAML for these assertions without adding a YAML dependency.
    /// </summary>
    private static string ComposeService(string compose, string serviceName)
    {
        var lines = compose.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, line => line == $"  {serviceName}:");

        Assert.True(start >= 0, $"docker-compose.yml has no '{serviceName}:' service.");

        var end = start + 1;
        while (end < lines.Length && (lines[end].Length == 0 || lines[end].StartsWith("   ", StringComparison.Ordinal)))
        {
            end++;
        }

        return string.Join('\n', lines[start..end]);
    }

    /// <summary>
    /// A committed file, found by walking up from the test binary: the test host's working directory
    /// is the build output, and these files are repository content rather than build output.
    /// </summary>
    private static string RepositoryFile(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, name);

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{name} was not found above {AppContext.BaseDirectory}.");
    }
}
