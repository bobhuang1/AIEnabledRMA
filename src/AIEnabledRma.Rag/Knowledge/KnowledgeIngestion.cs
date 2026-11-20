using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AIEnabledRma.Rag.Knowledge;

/// <summary>
/// Limits and rules for turning files on disk into articles the assistant may quote (F4).
/// </summary>
public sealed class KnowledgeIngestionOptions
{
    public const string SectionName = "Rag:Ingestion";

    /// <summary>Largest article file accepted, in bytes.</summary>
    public int MaxFileBytes { get; set; } = 64 * 1024;

    /// <summary>Most article files one load will read. More is an error, not a truncation.</summary>
    public int MaxArticles { get; set; } = 500;

    /// <summary>Largest front-matter block accepted, in bytes.</summary>
    public int MaxFrontMatterBytes { get; set; } = 4 * 1024;

    /// <summary>
    /// When true, an article without <c>reviewed_by</c> and <c>reviewed_at</c> is refused. The
    /// RAG host turns this on outside Development, so only reviewed text reaches customers.
    /// </summary>
    public bool RequireReviewed { get; set; }
}

/// <summary>Content hashing and prompt-injection containment for article bodies.</summary>
public static partial class KnowledgeSanitizer
{
    [GeneratedRegex(@"<!--[\s\S]*?-->", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"</?[A-Za-z][^>]{0,500}>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    // [text](target) and ![alt](target): keep the text, drop the target.
    [GeneratedRegex(@"!?\[(?<text>[^\]]{0,500})\]\([^)]{0,2000}\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BareUrlRegex();

    /// <summary>
    /// Text that reads as an instruction to a model rather than as troubleshooting content. An
    /// article containing any of these is refused at load: a reviewed article has no reason to
    /// talk to the model, so a match means it was tampered with or written carelessly.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:ignore|disregard|forget|override)\b[^.\n]{0,40}\b(?:previous|prior|above|earlier|all|system|your)\b[^.\n]{0,20}\b(?:instructions?|rules?|prompts?|guidelines?)\b"
        + @"|\byou are now\b|\bact as (?:an?|the)\b|\bnew instructions?\s*:"
        + @"|^\s*(?:system|assistant|developer)\s*:"
        + @"|<\|?(?:im_start|im_end|system|endoftext)\|?>"
        + @"|\[/?INST\]"
        + @"|\b(?:reveal|print|repeat|output)\b[^.\n]{0,30}\b(?:system prompt|hidden instructions?)\b"
        + @"|\bset\s+""?resolved""?\s*(?:to|=|:)\s*true\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex InstructionLikeRegex();

    /// <summary>
    /// SHA-256 of the body with line endings normalised, so a checkout with CRLF and one with LF
    /// agree on the hash. Lower-case hex.
    /// </summary>
    public static string ContentHash(string body)
    {
        var normalised = body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }

    /// <summary>Returns the instruction-like phrase found in <paramref name="text"/>, or null.</summary>
    public static string? FindInstructionLikeText(string text)
    {
        var match = InstructionLikeRegex().Match(text);
        return match.Success ? match.Value.Trim() : null;
    }

    /// <summary>
    /// Makes an article body safe to place inside the prompt's data block: HTML and comments
    /// removed, link targets and bare URLs dropped, and angle brackets replaced so the text can
    /// never close the <c>&lt;article&gt;</c>/<c>&lt;knowledge&gt;</c> delimiters it sits in.
    /// </summary>
    public static string Sanitize(string body)
    {
        var text = HtmlCommentRegex().Replace(body, string.Empty);
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = MarkdownLinkRegex().Replace(text, m => m.Groups["text"].Value);
        text = BareUrlRegex().Replace(text, "[link removed]");
        return NeutraliseDelimiters(text);
    }

    /// <summary>Replaces angle brackets with look-alikes that carry no markup meaning.</summary>
    public static string NeutraliseDelimiters(string text) =>
        text.Replace('<', '‹').Replace('>', '›');
}
