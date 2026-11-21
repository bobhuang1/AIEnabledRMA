using System.Diagnostics;
using AIEnabledRma.Ai;
using AIEnabledRma.Rag;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AIEnabledRma.Tests;

/// <summary>
/// A Production Web host that boots against a corpus which is not the reviewed one refuses to start.
/// </summary>
/// <remarks>
/// <see cref="KnowledgeBaseStartupCheck"/> is tested directly, by calling <c>StartAsync</c>, and
/// <c>AddRag</c> is tested for registering it. Neither answers the question a deployment asks: does a
/// host that reads the corpus at its real content root, through its real configuration, stop when the
/// corpus has drifted? A registration test passes with a hosted service nothing runs, and a direct
/// call passes with the check registered on a host that never starts it. This boots the host the
/// deployment runs — the Web image's entry point — with a corpus copied from the checkout and one
/// article edited.
/// </remarks>
public sealed class CorpusStartupBootTests
{
    private sealed class HostFactory(string corpusPath) : WebApplicationFactory<global::WebAppAnchor>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            // Unrelated Production guards that would fail the boot first, which would make the
            // assertion below pass for the wrong reason: the operator key, the fake gateway, and the
            // operator identity provider the host now requires in Production.
            builder.UseSetting(RagAdminGate.ApiKeyConfigurationKey, "production-operator-key");
            builder.UseSetting(
                $"{FakePaymentGatewayOptions.SectionName}:{nameof(FakePaymentGatewayOptions.AllowInProduction)}",
                "true");
            builder.UseSetting("OperatorAuth:Authority", "https://idp.example.com/realms/rma");
            builder.UseSetting("OperatorAuth:ClientId", "rma-operator");

            builder.UseSetting(
                $"{RagOptions.SectionName}:{nameof(RagOptions.KnowledgeBasePath)}",
                corpusPath);
        }
    }

    [Fact]
    public void A_production_host_refuses_a_corpus_that_does_not_match_its_manifest()
    {
        var corpus = DriftedCopyOfTheShippedCorpus();

        try
        {
            using var factory = new HostFactory(corpus);

            // The host starts inside CreateClient, so this is where a startup check surfaces.
            var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

            // The refusal has to name the manifest: an operator reading "the host will not start"
            // needs the file to restore, not a sentence about configuration.
            Assert.Contains("REVIEWED.sha256", exception.Message, StringComparison.Ordinal);
            Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(corpus, recursive: true);
        }
    }

    /// <summary>
    /// The real corpus, copied, with one article edited and its manifest left alone — which is what an
    /// article edited without a re-review looks like on disk.
    /// </summary>
    private static string DriftedCopyOfTheShippedCorpus()
    {
        var source = Path.Combine(RepositoryRoot(), "knowledge-base");
        var target = Path.Combine(Path.GetTempPath(), $"rma-corpus-{Guid.NewGuid():N}");

        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        var article = Directory.GetFiles(target, "*.md", SearchOption.AllDirectories)
            .First(path => !string.Equals(
                Path.GetFileName(path),
                "README.md",
                StringComparison.OrdinalIgnoreCase));

        File.AppendAllText(article, Environment.NewLine + "A sentence added after the review." + Environment.NewLine);

        return target;
    }

    /// <summary>
    /// Walks up from the test binary to the checkout. The build output holds a corpus copy of its own,
    /// so the search stops at the directory that also has the solution.
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
