using System.Globalization;
using System.Text;
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
        @"&#(?<value>[0-9]{1,7}|[xX][0-9a-fA-F]{1,6});",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex NumericEntity();

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

    /// <summary>
    /// The alphabet an instruction is allowed to arrive in: numeric entities decoded, invisible
    /// formatting characters removed, compatibility forms folded.
    /// </summary>
    /// <remarks>
    /// The instruction patterns match words, and a word can be spelled in ways that mean the same
    /// thing to a reader and nothing to a regular expression. "Ig&amp;#8203;nore" carries a
    /// zero-width space inside the word, "&amp;#73;gnore" spells the I as a decimal entity,
    /// "Ｉｇｎｏｒｅ" uses full-width letters. A reviewer reading the rendered article sees the
    /// same sentence every time; the matcher sees three different strings, and only one of them is
    /// caught. The folded text is what is stored, so the prompt receives what was checked.
    /// </remarks>
    internal static string Normalise(string text)
    {
        // Entities first: a decoded character may itself be invisible, and the strip below is what
        // removes it.
        var decoded = NumericEntity().Replace(text, DecodeNumericEntity);
        var visible = new StringBuilder(decoded.Length);

        foreach (var c in decoded)
        {
            // Format is the invisible half of Unicode: zero-width space, the joiners, bidi marks,
            // the word joiner, the soft hyphen. None of it is content, and any of it can be placed
            // inside a word that is otherwise an instruction.
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)
            {
                visible.Append(c);
            }
        }

        return FoldCompatibility(visible.ToString());
    }

    /// <summary>
    /// Folds the compatibility forms that can spell Latin text back to plain ASCII.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="string.Normalize()"/>: the hosts run in globalization-invariant
    /// mode, where NFKC returns its input unchanged, and taking an ICU dependency so a sanitiser
    /// can fold letters would change every culture-sensitive path in the application. What is
    /// folded here is the set that spells English words — full-width Latin and punctuation, Latin
    /// ligatures, the mathematical and letterlike "fancy font" alphabets, and circled or
    /// parenthesised letters. A general Unicode fold, and non-Latin homoglyphs, are out of scope:
    /// nothing here catches a Cyrillic "і" standing in for an "i".
    /// </remarks>
    private static string FoldCompatibility(string text)
    {
        StringBuilder? folded = null;

        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            var codePoint = char.IsHighSurrogate(current)
                && i + 1 < text.Length
                && char.IsLowSurrogate(text[i + 1])
                    ? char.ConvertToUtf32(current, text[i + 1])
                    : current;

            var width = codePoint > char.MaxValue ? 2 : 1;

            if (TryFold(codePoint, out var replacement))
            {
                // The builder is created lazily, so text with nothing to fold is returned as the
                // same instance rather than rebuilt character by character.
                folded ??= new StringBuilder(text.Length).Append(text, 0, i);
                folded.Append(replacement);
            }
            else
            {
                folded?.Append(text, i, width);
            }

            i += width - 1;
        }

        return folded?.ToString() ?? text;
    }

    /// <summary>
    /// The fold for one code point, if this table covers it.
    /// </summary>
    private static bool TryFold(int codePoint, out string replacement)
    {
        // Full-width Latin and punctuation are ASCII shifted by 0xFEE0, and the ideographic space
        // is the full-width space.
        if (codePoint is >= 0xFF01 and <= 0xFF5E)
        {
            replacement = ((char)(codePoint - 0xFEE0)).ToString();
            return true;
        }

        if (codePoint == 0x3000)
        {
            replacement = " ";
            return true;
        }

        if (codePoint is >= 0xFB00 and <= 0xFB06)
        {
            replacement = Ligatures[codePoint - 0xFB00];
            return true;
        }

        // Circled, parenthesised and negative-circled Latin letters.
        if (codePoint is >= 0x24B6 and <= 0x24CF)
        {
            replacement = ((char)('A' + codePoint - 0x24B6)).ToString();
            return true;
        }

        if (codePoint is >= 0x24D0 and <= 0x24E9)
        {
            replacement = ((char)('a' + codePoint - 0x24D0)).ToString();
            return true;
        }

        if (codePoint is >= 0x249C and <= 0x24B5)
        {
            replacement = ((char)('a' + codePoint - 0x249C)).ToString();
            return true;
        }

        // Mathematical alphanumeric symbols are complete runs of 52 letters and 10 digits, so the
        // offset within the run is the letter. The few letters Unicode puts elsewhere instead (they
        // are the holes in the script and double-struck runs) are in Letterlike below.
        foreach (var basis in MathLetterBases)
        {
            if (codePoint >= basis && codePoint < basis + 52)
            {
                var offset = codePoint - basis;
                replacement = (offset < 26
                    ? (char)('A' + offset)
                    : (char)('a' + offset - 26)).ToString();
                return true;
            }
        }

        foreach (var basis in MathDigitBases)
        {
            if (codePoint >= basis && codePoint < basis + 10)
            {
                replacement = ((char)('0' + codePoint - basis)).ToString();
                return true;
            }
        }

        if (Letterlike.TryGetValue(codePoint, out var letter))
        {
            replacement = letter.ToString();
            return true;
        }

        replacement = string.Empty;
        return false;
    }

    /// <summary>Latin ligatures, U+FB00 to U+FB06 in order.</summary>
    private static readonly string[] Ligatures = ["ff", "fi", "fl", "ffi", "ffl", "st", "st"];

    /// <summary>The first code point of each 52-letter mathematical alphanumeric run.</summary>
    private static readonly int[] MathLetterBases =
    [
        0x1D400, 0x1D434, 0x1D468, 0x1D49C, 0x1D4D0, 0x1D504, 0x1D538,
        0x1D56C, 0x1D5A0, 0x1D5D4, 0x1D608, 0x1D63C, 0x1D670,
    ];

    /// <summary>The first code point of each 10-digit mathematical alphanumeric run.</summary>
    private static readonly int[] MathDigitBases = [0x1D7CE, 0x1D7D8, 0x1D7E2, 0x1D7EC, 0x1D7F6];

    /// <summary>
    /// The letters Unicode keeps in the Letterlike Symbols block because the mathematical runs have
    /// holes where they would otherwise sit.
    /// </summary>
    private static readonly Dictionary<int, char> Letterlike = new()
    {
        [0x210E] = 'h',
        [0x210A] = 'g',
        [0x212F] = 'e',
        [0x2134] = 'o',
        [0x210B] = 'H',
        [0x2110] = 'I',
        [0x2112] = 'L',
        [0x211B] = 'R',
        [0x212C] = 'B',
        [0x2130] = 'E',
        [0x2131] = 'F',
        [0x2133] = 'M',
        [0x2102] = 'C',
        [0x210D] = 'H',
        [0x2115] = 'N',
        [0x2119] = 'P',
        [0x211A] = 'Q',
        [0x211D] = 'R',
        [0x2124] = 'Z',
    };

    /// <summary>
    /// Turns a numeric character reference into the character it names, and leaves anything that
    /// is not a valid scalar value alone: a truncated or out-of-range reference is not worth
    /// inventing a character for.
    /// </summary>
    private static string DecodeNumericEntity(Match match)
    {
        var value = match.Groups["value"].Value;
        var isHex = value[0] is 'x' or 'X';
        var digits = isHex ? value[1..] : value;

        int codePoint;
        var parsed = isHex
            ? int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codePoint)
            : int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out codePoint);

        // Surrogates are not characters, and a C0 control would put something a reader cannot see
        // into the prompt for no legitimate reason.
        if (!parsed
            || codePoint is < 0 or > 0x10FFFF
            || codePoint is >= 0xD800 and <= 0xDFFF
            || codePoint < 0x20 && codePoint is not ('\t' or '\n' or '\r'))
        {
            return match.Value;
        }

        return char.ConvertFromUtf32(codePoint);
    }

    public static (string Body, int InstructionLikeCount) Sanitise(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (string.Empty, 0);
        }

        // Normalised first, and the normalised text is what is stored: a pass that matches a
        // different string from the one the prompt receives hands the alphabet choice to the
        // author. See Normalise for what is folded away.
        var text = Normalise(body);
        text = HtmlComment().Replace(text, string.Empty);
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
