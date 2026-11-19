using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AIEnabledRma.Tests;

/// <summary>
/// What each host publishes is a decision about who reads the corpus, and both halves are asserted:
/// the Web host carries it, the tool server does not.
/// </summary>
/// <remarks>
/// The Web image ends with <c>COPY --from=build /app ./</c>: whatever publish omitted is missing at
/// runtime, and a host with no articles starts cleanly and answers every question as though the
/// corpus said nothing. The tool server is the opposite case. Its tools evaluate stored data through
/// the repositories, nothing in that process opens <c>knowledge-base/</c>, and the startup check that
/// inspects a corpus is registered by <see cref="RagServiceCollectionExtensions.AddRag"/>, which the
/// tool server does not call — but its publish output carried nineteen articles and the reviewed
/// manifest anyway. That made "the MCP image carries the corpus" true about bytes and false about
/// behaviour, which is the kind of check that passes while telling nobody anything.
/// </remarks>
public sealed class PublishLayoutTests
{
    [Fact]
    public async Task The_published_web_host_carries_the_corpus()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = RepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"rma-publish-{Guid.NewGuid():N}");

        try
        {
            await PublishAsync("AIEnabledRma.Web", root, output, ct);

            var published = Path.Combine(output, "knowledge-base");
            var manifest = Path.Combine(published, "REVIEWED.sha256");

            // The manifest is separate from the articles: without it a host that requires provenance
            // refuses to start, which is a different failure than an empty corpus but just as silent.
            Assert.True(File.Exists(manifest), "the published Web host has no knowledge-base/REVIEWED.sha256");

            var shipped = Directory.GetFiles(published, "*.md", SearchOption.AllDirectories);
            var checkedIn = Directory.GetFiles(Path.Combine(root, "knowledge-base"), "*.md", SearchOption.AllDirectories);

            // Every article, not just one: a partial copy silently changes what the retriever can
            // answer, and the count is the cheapest way to say so in the failure message.
            Assert.Equal(checkedIn.Length, shipped.Length);
            Assert.NotEmpty(shipped);
        }
        finally
        {
            Delete(output);
        }
    }

    /// <summary>
    /// The tool server publishes no corpus, and that is the assertion, not an oversight.
    /// </summary>
    /// <remarks>
    /// A publish that starts carrying the articles again would restore the claim this test's sibling
    /// removed: an image "with the corpus" whose host never reads it. If the tool server ever gains a
    /// corpus-backed tool it has to call <c>AddRag</c> as well, so the startup check runs, and this
    /// test is the place that decision is recorded.
    /// </remarks>
    [Fact]
    public async Task The_published_tool_server_carries_no_corpus()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = RepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"rma-publish-{Guid.NewGuid():N}");

        try
        {
            await PublishAsync("AIEnabledRma.Mcp", root, output, ct);

            Assert.False(
                Directory.Exists(Path.Combine(output, "knowledge-base")),
                "the tool server published a knowledge-base/ that nothing in that host reads; either it "
                + "gains a corpus-backed tool (with AddRag, so the startup check runs) or the project "
                + "stops declaring the content.");
        }
        finally
        {
            Delete(output);
        }
    }

    /// <summary>
    /// The image build restores from the Dockerfile's COPY list before it copies the tree, so a
    /// referenced project whose manifest is missing fails the restore with a message about an assets
    /// file — which reads as a publish problem rather than a missing line.
    /// </summary>
    /// <remarks>
    /// This is not hypothetical: the Web image could not be built at all because
    /// <c>AIEnabledRma.Privacy.csproj</c> was absent from the list while the Web project referenced
    /// it. Publishing from the source tree does not reproduce it, because there the whole checkout is
    /// present, so this test walks the references instead of trusting the file to stay complete.
    /// </remarks>
    [Theory]
    [InlineData("AIEnabledRma.Mcp")]
    [InlineData("AIEnabledRma.Web")]
    public void The_dockerfile_copies_every_project_it_builds(string project)
    {
        var root = RepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "src", project, "Dockerfile"));

        var copied = dockerfile
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("COPY ", StringComparison.Ordinal))
            .SelectMany(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var referenced in ReferencedProjects(Path.Combine(root, "src", project, project + ".csproj")))
        {
            var manifest = Path.Combine("src", referenced, referenced + ".csproj").Replace('\\', '/');

            Assert.True(
                copied.Contains(manifest),
                $"{project}'s Dockerfile restores without {manifest}, so `docker build` fails with an "
                + "assets-file error before the publish step runs. Add it to the COPY list above the restore.");
        }
    }

    /// <summary>
    /// Publishes one project into <paramref name="output"/> and fails with the tool's own output.
    /// </summary>
    private static async Task PublishAsync(
        string project,
        string root,
        string output,
        CancellationToken cancellationToken)
    {
        var publish = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in new[]
        {
            "publish",
            Path.Combine("src", project, project + ".csproj"),

            // Release, which is what the images publish, and also the only configuration that cannot
            // overwrite the Debug binaries this test is running out of — on Windows that lock would
            // fail the publish for a reason that has nothing to do with the corpus.
            "-c", "Release",
            "-o", output,
            "--no-restore",
        })
        {
            publish.ArgumentList.Add(argument);
        }

        using var process = Process.Start(publish)
            ?? throw new InvalidOperationException("could not start dotnet publish");

        // Both streams read concurrently: a build that fills one pipe buffer while the test waits on
        // the other deadlocks instead of failing.
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var log = await stdout + await stderr;

        Assert.True(process.ExitCode == 0, $"dotnet publish {project} failed:{Environment.NewLine}{log}");
    }

    private static void Delete(string output)
    {
        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }
    }

    /// <summary>Every project a project references, transitively, by assembly name.</summary>
    private static IReadOnlyList<string> ReferencedProjects(string projectFile)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        pending.Enqueue(projectFile);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();

            var directory = Path.GetDirectoryName(current)!;

            foreach (Match match in Regex.Matches(
                File.ReadAllText(current),
                "ProjectReference Include=\"([^\"]+)\""))
            {
                // The Include is relative to the referencing project, not to the solution.
                var reference = Path.GetFullPath(
                    Path.Combine(directory, match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)));

                var name = Path.GetFileNameWithoutExtension(reference);

                if (!seen.Add(name))
                {
                    continue;
                }

                found.Add(name);
                pending.Enqueue(reference);
            }
        }

        return found;
    }

    /// <summary>
    /// Walks up from the test binary to the checkout. The output directory carries a corpus copy of its
    /// own, so stopping at the first directory that has one would publish from a build artefact.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !(Directory.Exists(Path.Combine(directory.FullName, "knowledge-base"))
                    && File.Exists(Path.Combine(directory.FullName, "AIEnabledRma.slnx"))))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }
}
