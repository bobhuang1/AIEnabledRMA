using AIEnabledRma.Rag.Knowledge;

namespace AIEnabledRma.Rag;

/// <summary>
/// The startup rule for the initial corpus load: outside Development the host refuses to start
/// when that load produced errors or found no articles at all.
/// </summary>
/// <remarks>
/// The loader records its failures instead of throwing, and that is right in Development: the
/// host still starts, <c>/diagnostics</c> names every article that would not parse, and the reload
/// endpoint can swap a corrected corpus in without a restart. In a deployment the same facts become
/// the worst failure shape available — the process comes up, <c>/health</c> says ok, the container
/// platform reports a healthy service, and every triage answer is uncited because the corpus is
/// empty or half loaded. So outside Development the load result is a startup precondition, checked
/// where a deployment notices it, like <see cref="RagAdminGate.EnsureStartupAllowed"/> checks the
/// admin key.
/// </remarks>
public static class KnowledgeBaseGate
{
    /// <summary>How many per-file errors the refusal quotes before it summarises the rest.</summary>
    private const int QuotedErrors = 5;

    /// <summary>
    /// Throws unless the initial load is complete enough to serve. A corpus with no articles and a
    /// corpus with articles missing are both refused: each makes the assistant answer from less
    /// than the policy it is supposed to cite.
    /// </summary>
    public static void EnsureStartupAllowed(KnowledgeBaseLoadResult loaded, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        if (isDevelopment || (loaded.Articles.Count > 0 && loaded.Errors.Count == 0))
        {
            return;
        }

        var reasons = new List<string>();

        if (loaded.Articles.Count == 0)
        {
            reasons.Add("no articles were loaded");
        }

        if (loaded.Errors.Count > 0)
        {
            // Quoted, because the operator needs to know which article to fix, then summarised,
            // because a directory-level mistake can produce one error per file and a startup
            // exception nobody can read is its own failure.
            var quoted = loaded.Errors.Take(QuotedErrors).ToList();
            var remaining = loaded.Errors.Count - quoted.Count;
            var suffix = remaining > 0 ? $" (and {remaining} more)" : string.Empty;

            reasons.Add(
                $"{loaded.Errors.Count} load error(s){suffix}: {string.Join(" | ", quoted)}");
        }

        throw new InvalidOperationException(
            $"The knowledge base at {loaded.RootPath} is not usable: {string.Join("; ", reasons)}. "
            + "Outside Development the host refuses to start on a corpus that would make every "
            + "answer uncited or let the assistant answer from part of the policy. Fix the "
            + "articles or the configured Rag:KnowledgeBasePath; run in Development to inspect "
            + "the per-file reasons through /diagnostics.");
    }
}
