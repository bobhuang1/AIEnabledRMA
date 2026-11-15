using AIEnabledRma.Rag.Knowledge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// The corpus under <c>knowledge-base/</c> is hand-written, reviewed, and then read into a prompt.
/// The loader reports what it refuses, but nothing obliged anyone to look at those reports before
/// a deployment: an article with a broken front-matter block, or a paragraph written at the model
/// rather than the reader, would load as a slightly smaller or slightly different corpus and the
/// assistant would answer from it anyway.
/// </summary>
/// <remarks>
/// So the real directory is loaded here rather than a fixture. This is the test that fails in a
/// pull request that adds an article the loader cannot read, or one whose text the sanitiser has
/// to neutralise, instead of the failure surfacing as a citation that quietly stops appearing.
/// </remarks>
public sealed class ShippedKnowledgeBaseTests
{
    [Fact]
    public void Every_shipped_article_loads_with_no_errors_and_nothing_neutralised()
    {
        var root = KnowledgeBaseDirectory();
        var loader = new KnowledgeBaseLoader(
            NullLogger<KnowledgeBaseLoader>.Instance,
            Options.Create(new KnowledgeBaseOptions()));

        var result = loader.Load(root);

        // One message carrying every reason, because a corpus that drops two articles usually has
        // one cause and the first error alone is enough to send the author to the wrong file.
        Assert.True(
            result.Errors.Count == 0,
            $"the shipped corpus reported {result.Errors.Count} load error(s):{Environment.NewLine}"
            + string.Join(Environment.NewLine, result.Errors));

        // An empty corpus would also report no errors, and would leave every retrieval answer
        // uncited rather than obviously broken, so the article count is part of the assertion.
        Assert.NotEmpty(result.Articles);

        var neutralised = result.Articles
            .Where(a => a.InstructionLikeCount > 0)
            .Select(a => $"{a.Id} in {a.SourcePath}: {a.InstructionLikeCount} line(s)")
            .ToList();

        Assert.True(
            neutralised.Count == 0,
            "the sanitiser neutralised text in the shipped corpus, which means an article is "
            + "addressing the model rather than the reader:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, neutralised));

        // The count is a summary; the placeholder is what actually reaches the prompt. Checking
        // both means a future change to the counting cannot make a neutralised line invisible.
        Assert.DoesNotContain(
            result.Articles,
            a => a.Body.Contains(ArticleSanitizer.NeutralisedLine, StringComparison.Ordinal)
                 || a.Title.Contains(ArticleSanitizer.NeutralisedLine, StringComparison.Ordinal));
    }

    /// <summary>
    /// Walks up from the test binary to the repository root, because the suite can be launched from
    /// a shell, an IDE, or CI and none of those share a working directory. The directory is
    /// asserted to exist rather than silently loading nothing if the corpus is ever moved.
    /// </summary>
    /// <remarks>
    /// The walk cannot stop at the first directory holding a <c>knowledge-base</c> folder: the RAG
    /// and Web projects copy the corpus into their build output, so the test binary sits next to a
    /// copy that is only as fresh as the last build. The root is the directory that also holds the
    /// solution, which means this reads the files under review.
    /// </remarks>
    private static string KnowledgeBaseDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !(Directory.Exists(Path.Combine(directory.FullName, "knowledge-base"))
                    && File.Exists(Path.Combine(directory.FullName, "AIEnabledRma.slnx"))))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!.FullName, "knowledge-base");
    }
}
