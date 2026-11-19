using System.Diagnostics;

namespace AIEnabledRma.Tests;

/// <summary>
/// The corpus has to survive <c>dotnet publish</c>, because that is the only copy a container gets.
/// </summary>
/// <remarks>
/// The tool server's image is <c>COPY --from=build /app ./</c>: whatever publish omitted is missing at
/// runtime, and a host with no articles starts cleanly and answers every question as though the corpus
/// said nothing. Adding the files to the RAG project was not enough — a ProjectReference publishes its
/// assembly, not another project's <c>CopyToPublishDirectory</c> content — so this test asks publish
/// itself rather than reading the project files, which is the difference between checking the
/// declaration and checking the artefact.
/// </remarks>
public sealed class PublishLayoutTests
{
    [Theory]
    [InlineData("AIEnabledRma.Mcp")]
    [InlineData("AIEnabledRma.Web")]
    public async Task The_published_host_carries_the_corpus(string project)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = RepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"rma-publish-{Guid.NewGuid():N}");

        try
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

                // Release, which is what the images publish, and also the only configuration that
                // cannot overwrite the Debug binaries this test is running out of — on Windows that
                // lock would fail the publish for a reason that has nothing to do with the corpus.
                "-c", "Release",
                "-o", output,
                "--no-restore",
            })
            {
                publish.ArgumentList.Add(argument);
            }

            using var process = Process.Start(publish)
                ?? throw new InvalidOperationException("could not start dotnet publish");

            // Both streams read concurrently: a build that fills one pipe buffer while the test waits
            // on the other deadlocks instead of failing.
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            var log = await stdout + await stderr;

            Assert.True(process.ExitCode == 0, $"dotnet publish {project} failed:{Environment.NewLine}{log}");

            var published = Path.Combine(output, "knowledge-base");
            var manifest = Path.Combine(published, "REVIEWED.sha256");

            // The manifest is separate from the articles: without it a host that requires provenance
            // refuses to start, which is a different failure than an empty corpus but just as silent.
            Assert.True(File.Exists(manifest), $"the published {project} has no knowledge-base/REVIEWED.sha256");

            var shipped = Directory.GetFiles(published, "*.md", SearchOption.AllDirectories);
            var checkedIn = Directory.GetFiles(Path.Combine(root, "knowledge-base"), "*.md", SearchOption.AllDirectories);

            // Every article, not just one: a partial copy silently changes what the retriever can
            // answer, and the count is the cheapest way to say so in the failure message.
            Assert.Equal(checkedIn.Length, shipped.Length);
            Assert.NotEmpty(shipped);
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
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
