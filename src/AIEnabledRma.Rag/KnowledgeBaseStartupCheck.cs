using AIEnabledRma.Rag.Knowledge;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AIEnabledRma.Rag;

/// <summary>
/// The one place a host says, at startup, how much corpus it actually loaded and whether that corpus
/// is the reviewed one.
/// </summary>
/// <remarks>
/// Registered by <see cref="RagServiceCollectionExtensions.AddRag"/>, so the Web host and the RAG host
/// both run it — before this, only the RAG host checked its load, and only for load errors. A host
/// that comes up with an empty or drifted corpus answers every question as though the policy said
/// nothing about it, and nothing in the logs or on <c>/health</c> says so. Outside Development that is
/// a startup failure; inside it, the count and the reason are logged and the host keeps starting so
/// the corpus can be inspected rather than crash-looped.
/// </remarks>
public sealed class KnowledgeBaseStartupCheck(
    KnowledgeBaseLoadResult loaded,
    IHostEnvironment environment,
    ILogger<KnowledgeBaseStartupCheck> logger) : IHostedService
{
    /// <summary>How many problems the refusal quotes before it summarises the rest.</summary>
    private const int QuotedProblems = 5;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var verification = KnowledgeBaseVerifier.Verify(loaded);

        var problems = new List<string>(verification.Problems);

        // The loader's own errors are part of the same question: an article it refused is an article
        // the caller cannot be told about, whether or not the manifest still covers it.
        problems.AddRange(loaded.Errors.Select(error => $"load error: {error}"));

        // Always logged, in both environments: the count is the fact that turns "the corpus is
        // shipped" into something an operator can check against a manifest without opening a shell.
        logger.LogInformation(
            "Knowledge base at {Root}: {Articles} article(s), {Entries} line(s) in {Manifest}, {Problems} problem(s).",
            loaded.RootPath,
            verification.Articles,
            verification.ManifestEntries,
            KnowledgeBaseLoader.ReviewedHashesFileName,
            problems.Count);

        if (problems.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var problem in problems)
        {
            logger.LogError("Knowledge base check: {Problem}", problem);
        }

        if (environment.IsDevelopment())
        {
            logger.LogWarning(
                "The knowledge base at {Root} does not match {Manifest}, and the host is starting anyway "
                + "because this is Development. Retrieval will answer from what is on disk, which is not "
                + "the reviewed corpus. Run in a non-Development environment to make this a startup failure.",
                loaded.RootPath,
                KnowledgeBaseLoader.ReviewedHashesFileName);

            return Task.CompletedTask;
        }

        var quoted = problems.Take(QuotedProblems).ToList();
        var remaining = problems.Count - quoted.Count;
        var suffix = remaining > 0 ? $" (and {remaining} more)" : string.Empty;

        throw new InvalidOperationException(
            $"The knowledge base at {loaded.RootPath} does not match "
            + $"{KnowledgeBaseLoader.ReviewedHashesFileName}: {problems.Count} problem(s){suffix}: "
            + $"{string.Join(" | ", quoted)}. "
            + "An empty or drifted corpus answers every question as though the policy said nothing, so "
            + "outside Development the host refuses to start. Restore the corpus and its manifest from "
            + "the reviewed change, or run in Development to inspect it.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
