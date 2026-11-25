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
public sealed class PublishLayoutTests : IClassFixture<PublishLayoutFixture>
{
    private readonly PublishLayoutFixture _publish;

    /// <remarks>
    /// The fixture publishes the three hosts once, in one invocation, before any test in this class
    /// runs. Each test below reads that output rather than publishing its own copy, so the three
    /// assertions judge the same bytes and the shared dependency graph is built once.
    /// </remarks>
    public PublishLayoutTests(PublishLayoutFixture publish) => _publish = publish;

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
    public void The_published_web_host_carries_the_corpus()
    {
        _publish.RequirePublished();

        var published = Path.Combine(_publish.Output(PublishLayoutFixture.Web), "knowledge-base");
        var manifest = Path.Combine(published, "REVIEWED.sha256");

        // The manifest is separate from the articles: without it a host that requires provenance
        // refuses to start, which is a different failure than an empty corpus but just as silent.
        Assert.True(File.Exists(manifest), "the published Web host has no knowledge-base/REVIEWED.sha256");

        var shipped = Directory.GetFiles(published, "*.md", SearchOption.AllDirectories);
        var checkedIn = Directory.GetFiles(
            Path.Combine(_publish.RepositoryRoot, "knowledge-base"),
            "*.md",
            SearchOption.AllDirectories);

        // Every article, not just one: a partial copy silently changes what the retriever can
        // answer, and the count is the cheapest way to say so in the failure message.
        Assert.Equal(checkedIn.Length, shipped.Length);
        Assert.NotEmpty(shipped);
    }

    /// <summary>
    /// The RAG host reads the corpus and opens PostgreSQL through Npgsql; it never builds an EF
    /// model, so its image carries no ORM.
    /// </summary>
    /// <remarks>
    /// The shared Production database rule used to live in the data layer, which made this project
    /// reference it and put Entity Framework Core into an image that needs none. The rule now lives in
    /// Domain, beside the credential rules it applies. This asserts the published output rather than
    /// the project file: a reference pulled in through another project would pass a csproj check and
    /// still ship the assemblies, and an image is the thing that has to stay small.
    /// </remarks>
    [Fact]
    public void The_published_rag_host_carries_no_orm()
    {
        _publish.RequirePublished();

        // Published at all, before anything is claimed about what it holds: an output directory that
        // was never written satisfies "carries no ORM" by being empty, and a shared invocation that
        // dropped this host would pass here and fail only in an image build.
        Assert.True(
            Directory.Exists(_publish.Output(PublishLayoutFixture.Rag))
            && Directory.GetFiles(_publish.Output(PublishLayoutFixture.Rag)).Length > 0,
            "the shared publish wrote nothing for the RAG host");

        var published = Directory.GetFiles(_publish.Output(PublishLayoutFixture.Rag))
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("AIEnabledRma.Data.dll", published);
        Assert.DoesNotContain("Npgsql.EntityFrameworkCore.PostgreSQL.dll", published);
        Assert.DoesNotContain(
            published,
            file => file.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));

        // The vector store still talks to PostgreSQL directly, so the assertions above are about the
        // ORM rather than about a publish that lost database access altogether.
        Assert.Contains("Npgsql.dll", published);
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
    public void The_published_tool_server_carries_no_corpus()
    {
        _publish.RequirePublished();

        // The same precondition, and for the same reason: "no knowledge-base/" is true of a publish
        // that never ran, so the tool server has to have published something for its silence about
        // the corpus to mean anything.
        Assert.True(
            Directory.Exists(_publish.Output(PublishLayoutFixture.Mcp))
            && Directory.GetFiles(_publish.Output(PublishLayoutFixture.Mcp)).Length > 0,
            "the shared publish wrote nothing for the tool server");

        Assert.False(
            Directory.Exists(Path.Combine(_publish.Output(PublishLayoutFixture.Mcp), "knowledge-base")),
            "the tool server published a knowledge-base/ that nothing in that host reads; either it "
            + "gains a corpus-backed tool (with AddRag, so the startup check runs) or the project "
            + "stops declaring the content.");
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
    [InlineData("AIEnabledRma.Rag")]
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
        var publish = PublishLayoutFixture.Invocation(
            traversal: "publish-hosts.proj",
            layoutRoot: "out",
            layoutProps: "layout.props");

        Assert.True(publish.RedirectStandardOutput);
        Assert.True(publish.RedirectStandardError);
        Assert.Equal("1", publish.Environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("0", publish.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
        Assert.Contains("-p:UseSharedCompilation=false", publish.ArgumentList);

        // And it is one invocation for all three hosts, which is the point: three `dotnet publish`
        // runs spent 88 seconds on a cold tree building the same graph three times, and one
        // invocation measured 69 with identical output.
        Assert.Contains("-t:PublishAll", publish.ArgumentList);
        Assert.Contains(publish.ArgumentList, a => a.StartsWith("-p:PublishLayoutRoot=", StringComparison.Ordinal));
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
    /// The checkout the shared publish ran from, found by the fixture so that the file-reading tests
    /// and the publish cannot disagree about which tree is under test.
    /// </summary>
    private static string RepositoryRoot() => PublishLayoutFixture.FindRepositoryRoot();
}
