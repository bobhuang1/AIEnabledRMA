using System.Diagnostics;

namespace AIEnabledRma.Tests;

/// <summary>
/// Publishes the three hosts once, in a single MSBuild invocation, and hands the outputs to the
/// tests that read them.
/// </summary>
/// <remarks>
/// The three hosts publish from three different projects, so there is no one <c>dotnet publish</c>
/// whose output all three tests could read: what can be shared is the invocation. Three separate
/// <c>dotnet publish</c> runs cost 88 seconds on a cold tree, measured in the SDK container the suite
/// uses, and almost all of it is spent building the same dependency graph three times over — the
/// second and third runs still evaluate every project and still pay for the copy. One
/// <c>dotnet msbuild</c> that calls <c>Publish</c> on all three measures 69 seconds, with identical
/// outputs: 38 files and the ten corpus entries for the Web host, 13 for Rag, 29 and no
/// <c>knowledge-base/</c> for the tool server.
///
/// <para>
/// Each host still gets its own output directory, because that is the thing being asserted: a publish
/// that put the corpus in the tool server's image would be invisible if all three wrote to one place.
/// <see cref="PublishDir"/> is therefore set per project by a props file the child builds import,
/// keyed on the project's own name.
/// </para>
///
/// <para>
/// The publish runs once for the test class rather than per test. That matters for more than time: it
/// gives every assertion the same output to judge, so a difference between "Web carries the corpus"
/// and "the tool server does not" cannot come from two publishes disagreeing.
/// </para>
/// </remarks>
public sealed class PublishLayoutFixture : IAsyncLifetime, IDisposable
{
    public const string Web = "AIEnabledRma.Web";
    public const string Rag = "AIEnabledRma.Rag";
    public const string Mcp = "AIEnabledRma.Mcp";

    /// <summary>
    /// A cold Release build of the three hosts and their references fits comfortably; a publish that
    /// has not finished by then is failing the gate anyway, and it must fail with a message rather
    /// than hold the run open until a leaked child decides to exit.
    /// </summary>
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(10);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "rma-publish-" + Guid.NewGuid().ToString("N"));

    public PublishLayoutFixture()
    {
        RepositoryRoot = FindRepositoryRoot();
    }

    public string RepositoryRoot { get; }

    /// <summary>The exit code of the shared publish, or -1 if it never ran.</summary>
    public int ExitCode { get; private set; } = -1;

    /// <summary>What the shared publish said, both streams, for a failure message.</summary>
    public string Log { get; private set; } = string.Empty;

    public bool Succeeded => ExitCode == 0;

    /// <summary>Where the named project published, e.g. <c>…/AIEnabledRma.Web</c>.</summary>
    public string Output(string project) => Path.Combine(_root, project);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var traversal = Path.Combine(_root, "publish-hosts.proj");
        var layout = Path.Combine(_root, "publish-layout.props");

        await File.WriteAllTextAsync(traversal, TraversalProject(RepositoryRoot));
        await File.WriteAllTextAsync(layout, LayoutProps);

        using var process = Process.Start(Invocation(traversal, _root, layout))
            ?? throw new InvalidOperationException("could not start dotnet msbuild");

        // Both streams read concurrently: a build that fills one pipe buffer while the test waits on
        // the other deadlocks instead of failing.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var deadline = new CancellationTokenSource(PublishTimeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token);
            Log = await stdout.WaitAsync(deadline.Token) + await stderr.WaitAsync(deadline.Token);
            ExitCode = process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new InvalidOperationException(
                $"the shared publish of {Web}, {Rag} and {Mcp} did not finish within"
                + $" {PublishTimeout.TotalMinutes:0} minutes. A child that outlives the publish keeps"
                + " the redirected output pipe open, so the log never reaches EOF; node reuse and the"
                + " shared compiler are disabled to prevent that.");
        }
    }

    /// <summary>Fails the calling test with the publish's own output if it did not succeed.</summary>
    public void RequirePublished()
    {
        if (!Succeeded)
        {
            throw new InvalidOperationException(
                $"the shared publish of {Web}, {Rag} and {Mcp} failed with exit code {ExitCode}:"
                + Environment.NewLine + Log);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The invocation the tests also assert on: a publish that leaves an MSBuild node behind leaves
    /// its output pipes open as well, because the node inherits the redirected handles and outlives
    /// the process whose output is being read. The parent exits, the node does not, and the read
    /// waits for an EOF that arrives only when the node reaches its idle timeout. That is a silent
    /// quarter of an hour on a workstation with the test host alive and idle, and it is the
    /// local-only shape of the stall: the same suite on a fresh CI runner passes, which made it look
    /// like a flake rather than a leaked child.
    /// </summary>
    public static ProcessStartInfo Invocation(string traversal, string layoutRoot, string layoutProps)
    {
        var publish = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(traversal)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in new[]
        {
            "msbuild",
            traversal,
            "-t:PublishAll",
            "-nologo",

            // The child builds inherit this, and Release is what the images publish. It is also the
            // only configuration that cannot overwrite the Debug binaries this test is running out
            // of — on Windows that lock would fail the publish for a reason that has nothing to do
            // with the corpus.
            "-p:Configuration=Release",
            "-p:UseSharedCompilation=false",

            // Named separately rather than interpolated into the projects' own builds: each host's
            // PublishDir comes from the props file, keyed on its own project name.
            $"-p:PublishLayoutRoot={layoutRoot}",
            $"-p:LayoutProps={layoutProps}",
        })
        {
            publish.ArgumentList.Add(argument);
        }

        publish.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        publish.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        return publish;
    }

    /// <summary>
    /// One target, one build graph: the three hosts are published by a single MSBuild invocation, so
    /// the shared projects they reference are built once and the three publishes cannot disagree
    /// about what those assemblies contained.
    /// </summary>
    private static string TraversalProject(string repositoryRoot) =>
        $"""
        <Project DefaultTargets="PublishAll">

          <!-- Written by PublishLayoutFixture at run time: absolute paths, so the file can live in a
               temporary directory without depending on where it was started from. -->
          <ItemGroup>
            <Host Include="{repositoryRoot}/src/{Web}/{Web}.csproj" />
            <Host Include="{repositoryRoot}/src/{Rag}/{Rag}.csproj" />
            <Host Include="{repositoryRoot}/src/{Mcp}/{Mcp}.csproj" />
          </ItemGroup>

          <Target Name="PublishAll">
            <!-- BuildInParallel, because the three hosts share their references and MSBuild builds
                 each of them once per invocation; the three publishes are otherwise independent. -->
            <MSBuild Projects="@(Host)"
                     Targets="Publish"
                     BuildInParallel="true"
                     Properties="Configuration=Release;UseSharedCompilation=false;PublishLayoutRoot=$(PublishLayoutRoot);CustomBeforeMicrosoftCommonTargets=$(LayoutProps)" />
          </Target>

        </Project>
        """;

    /// <summary>
    /// Imported by every child build, before the common targets resolve <c>PublishDir</c>, which the
    /// SDK only defaults when it is empty.
    /// </summary>
    private const string LayoutProps =
        """
        <Project>

          <PropertyGroup Condition="'$(PublishLayoutRoot)' != ''">
            <PublishDir>$(PublishLayoutRoot)/$(MSBuildProjectName)/</PublishDir>
          </PropertyGroup>

        </Project>
        """;

    /// <summary>
    /// Walks up from the test binary to the checkout. The output directory carries a corpus copy of
    /// its own, so stopping at the first directory that has one would publish from a build artefact.
    /// </summary>
    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !(Directory.Exists(Path.Combine(directory.FullName, "knowledge-base"))
                    && File.Exists(Path.Combine(directory.FullName, "AIEnabledRma.slnx"))))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("could not find the checkout above " + AppContext.BaseDirectory);
        }

        return directory.FullName;
    }
}
