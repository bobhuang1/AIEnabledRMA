using System.Text.RegularExpressions;

namespace AIEnabledRma.Rag.Knowledge;

/// <summary>
/// Cleans article text before it can reach a prompt.
/// </summary>
/// <remarks>
/// The knowledge base is hand-written and code-reviewed, which makes it trusted at the process
/// level and not at the prompt level. The model cannot tell an instruction from a paragraph, so
/// an article that says "ignore the reference material" reads exactly like one that says
/// "hold the power button". Three things are removed here:
///
/// <list type="bullet">
/// <item>HTML, including comments and tags. A tag is markup the assistant cannot use and a
/// comment is text nobody sees in review but the model would.</item>
/// <item>Link targets. A URL inside a prompt is a place the model could be told to fetch, and
/// the corpus has no legitimate need for one; the link text stays.</item>
/// <item>Lines that address the model rather than the reader. The whole line goes, because a
/// half-removed instruction is still an instruction.</item>
/// </list>
/// </remarks>
public static partial class ArticleSanitizer
{
    [GeneratedRegex(@"<!--[\s\S]*?-->", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"<[^>\n]{0,2000}>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"\[(?<text>[^\]]+)\]\([^)]*\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(
        @"https?://[^\s<>()\[\]""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex BareUrl();

    /// <summary>
    /// The shapes an article uses when it is written to the model instead of the reader.
    /// Kept as a list of predicates rather than one alternation so the reason a line was
    /// removed stays readable.
    /// </summary>
    private static readonly Regex[] InstructionPatterns =
    [
        new(@"\b(ignore|disregard|forget)\b.{0,30}\b(previous|prior|above|earlier)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)),
        new(@"\byou are now\b|\bfrom now on,? you\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)),
        new(@"^\s*(system|assistant|developer|user)\s*:",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)),
        new(@"<\|(im_start|im_end|endoftext)\|>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)),
        new(@"\bnew instructions?\b|\boverride (the )?(system|previous) prompt\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)),
        new(@"\breveal (your|the) (system )?prompt\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)),
    ];

    public const string NeutralisedLine = "[instruction-like text removed]";

    public static (string Body, int InstructionLikeCount) Sanitise(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (string.Empty, 0);
        }

        var text = HtmlComment().Replace(body, string.Empty);
        text = MarkdownImage().Replace(text, string.Empty);
        text = HtmlTag().Replace(text, string.Empty);
        text = MarkdownLink().Replace(text, match => match.Groups["text"].Value);
        text = BareUrl().Replace(text, "[link removed]");
        text = text.Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase);

        var neutralised = 0;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (InstructionPatterns.Any(p => p.IsMatch(lines[i])))
            {
                lines[i] = NeutralisedLine;
                neutralised++;
            }
        }

        var cleaned = string.Join('\n', lines).Trim();

        // Collapse the blank-line runs a removed block leaves behind, so the snippet budget is
        // spent on prose rather than on whitespace.
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n", RegexOptions.CultureInvariant);

        return (cleaned, neutralised);
    }
}
