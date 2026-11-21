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
    /// <summary>
    /// The corpus is declared once, in <c>Directory.Build.props</c>, for the hosts that read it. A
    /// project that declares the same files again publishes them twice and drifts from that one list.
    /// </summary>
    /// <remarks>
    /// The declaration moved out of the Rag and Web projects because the two copies were identical and
    /// only one of them was ever updated with the manifest. This fails on a project that declares an
    /// <c>Include</c> under <c>knowledge-base</c>, not on one that mentions it: the tool server's
    /// project explains at length why it carries none, and that comment is the record, not a duplicate.
    /// </remarks>
    [Fact]
    public void The_corpus_is_declared_once_for_the_hosts_that_read_it()
    {
        var root = RepositoryRoot();
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));

        Assert.Contains("knowledge-base/**/*.md", props, StringComparison.Ordinal);
        Assert.Contains("knowledge-base/REVIEWED.sha256", props, StringComparison.Ordinal);

        // Named in the condition, so the set of consumers is visible where the declaration is: the
        // two hosts that read the corpus at startup, and the ingest tool that reads it next to itself.
        Assert.Contains("'$(MSBuildProjectName)' == 'AIEnabledRma.Web'", props, StringComparison.Ordinal);
        Assert.Contains("'$(MSBuildProjectName)' == 'AIEnabledRma.Rag'", props, StringComparison.Ordinal);
        Assert.Contains("'$(MSBuildProjectName)' == 'AIEnabledRma.Tools.DbAdmin'", props, StringComparison.Ordinal);

        var offenders = Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(project => !project.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                              && !project.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(project => File.ReadAllLines(project).Any(line =>
                line.Contains("knowledge-base", StringComparison.OrdinalIgnoreCase)
                && line.Contains("Include=", StringComparison.OrdinalIgnoreCase)))
            .Select(project => Path.GetRelativePath(root, project))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "these projects declare knowledge-base content of their own: " + string.Join(", ", offenders)
            + ". Declare it once in Directory.Build.props, which copies it for the Web and RAG hosts.");
    }

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
    /// A locked restore reads every project's lock file from the COPY list, so a missing one fails
    /// the image build; without `--locked-mode` the build resolves whatever the feed offers, which is
    /// a set of packages the suite never tested.
    /// </summary>
    /// <remarks>
    /// The RAG image did this from the start and the other two did not, which is the kind of
    /// difference a README sentence hides: all three images claimed to build the same committed
    /// graph, and only one enforced it. The project itself is included because its own lock file is
    /// part of the restore, not only its references'.
    /// </remarks>
    [Theory]
    [InlineData("AIEnabledRma.Mcp")]
    [InlineData("AIEnabledRma.Web")]
    [InlineData("AIEnabledRma.Rag")]
    public void The_dockerfile_restores_in_locked_mode_from_the_committed_lock_files(string project)
    {
        var root = RepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "src", project, "Dockerfile"));

        var copied = dockerfile
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("COPY ", StringComparison.Ordinal))
            .SelectMany(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var projects = ReferencedProjects(Path.Combine(root, "src", project, project + ".csproj"))
            .Prepend(project);

        foreach (var dependency in projects)
        {
            var lockFile = $"src/{dependency}/packages.lock.json";

            Assert.True(
                copied.Contains(lockFile),
                $"{project}'s Dockerfile restores in locked mode without {lockFile}, so `docker build` "
                + "fails before the publish step runs. Copy the lock file beside the manifest.");
        }

        Assert.Contains(
            $"dotnet restore src/{project}/{project}.csproj --locked-mode",
            dockerfile,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The publish's output is read to EOF, so a child that outlives the publish and inherits its
    /// redirected handles keeps that read open. MSBuild node reuse and the shared compiler are the
    /// two that do that, and this pins the settings that keep the read bounded.
    /// </summary>
    [Fact]
    public void A_publish_does_not_leave_a_child_holding_its_output_pipe()
    {
        var publish = PublishStartInfo("AIEnabledRma.Web", root: ".", output: "out");

        Assert.True(publish.RedirectStandardOutput);
        Assert.True(publish.RedirectStandardError);
        Assert.Equal("1", publish.Environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("0", publish.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
        Assert.Contains("-p:UseSharedCompilation=false", publish.ArgumentList);
    }

    /// <summary>
    /// A publish that leaves an MSBuild node behind leaves its output pipes open as well: the node
    /// inherits the redirected handles and outlives the process whose output is being read. The
    /// parent exits, the node does not, and the read below waits for an EOF that arrives only when
    /// the node reaches its idle timeout. That is a silent quarter of an hour on a workstation with
    /// the test host alive and idle, and it is the local-only shape of the stall: the same suite on
    /// a fresh CI runner passes, which made it look like a flake rather than a leaked child. Reuse
    /// is therefore off, the shared compiler server (the same kind of child) is off for this build,
    /// and the CLI is told not to start a server that would survive the publish for the same reason.
    /// </summary>
    private static ProcessStartInfo PublishStartInfo(string project, string root, string output)
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
            "-p:UseSharedCompilation=false",
        })
        {
            publish.ArgumentList.Add(argument);
        }

        publish.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        publish.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        return publish;
    }

    /// <summary>
    /// A cold Release build of every referenced project fits comfortably; a publish that has not
    /// finished by then is failing the gate anyway, and it must fail with a message rather than
    /// hold the run open until a leaked child decides to exit.
    /// </summary>
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Publishes one project into <paramref name="output"/> and fails with the tool's own output.
    /// </summary>
    private static async Task PublishAsync(
        string project,
        string root,
        string output,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(PublishStartInfo(project, root, output))
            ?? throw new InvalidOperationException("could not start dotnet publish");

        // Both streams read concurrently: a build that fills one pipe buffer while the test waits on
        // the other deadlocks instead of failing.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(PublishTimeout);

        string log;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            log = await stdout.WaitAsync(deadline.Token) + await stderr.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new InvalidOperationException(
                $"dotnet publish {project} did not finish within {PublishTimeout.TotalMinutes:0} minutes. "
                + "A child that outlives the publish keeps the redirected output pipe open, so the log "
                + "never reaches EOF; node reuse and the shared compiler are disabled to prevent that.");
        }

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
