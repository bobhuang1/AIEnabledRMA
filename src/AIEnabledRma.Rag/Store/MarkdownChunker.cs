using System.Text;
using System.Text.RegularExpressions;

namespace AIEnabledRma.Rag.Store;

/// <summary>One retrievable piece of a document.</summary>
public sealed record KnowledgeChunk(int Ordinal, string HeadingPath, string Text, int TokenCount)
{
    /// <summary>What is embedded and full-text indexed: the heading path gives a chunk its context.</summary>
    public string IndexText => HeadingPath.Length == 0 ? Text : HeadingPath + "\n\n" + Text;
}

public sealed class ChunkingOptions
{
    public const string SectionName = "Rag:Chunking";

    /// <summary>Chunks are filled towards this size.</summary>
    public int TargetTokens { get; set; } = 350;

    /// <summary>No chunk grows past this unless a single list would have to be cut.</summary>
    public int MaxTokens { get; set; } = 500;

    /// <summary>Sections smaller than this are merged into the next one in the same document.</summary>
    public int MinTokens { get; set; } = 60;

    /// <summary>Overlap carried into the next chunk of a long section, as a fraction of <see cref="MaxTokens"/>.</summary>
    public double OverlapFraction { get; set; } = 0.15;
}

/// <summary>
/// Structure-aware markdown chunking. Splits on headings and keeps the article title and heading
/// path as each chunk's prefix; packs paragraphs up to the target size; never splits a list
/// (troubleshooting steps stay together) or a code block; carries a short overlap between chunks
/// of one long section.
/// </summary>
/// <remarks>
/// Sizes are estimated as 4/3 tokens per word, which is close for English with the common
/// embedding tokenizers. A tokenizer-exact count would tie the chunker to one model (plan D4).
/// </remarks>
public static partial class MarkdownChunker
{
    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9])")]
    private static partial Regex SentenceBoundaryRegex();

    public static int EstimateTokens(string text) =>
        (int)Math.Ceiling(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length * 4d / 3d);

    public static IReadOnlyList<KnowledgeChunk> Chunk(string title, string markdown, ChunkingOptions? options = null)
    {
        options ??= new ChunkingOptions();
        var sections = MergeSmall(Sections(title, markdown ?? string.Empty), options);

        var chunks = new List<KnowledgeChunk>();
        foreach (var (path, blocks) in sections)
        {
            foreach (var text in Pack(blocks, options))
            {
                chunks.Add(new KnowledgeChunk(chunks.Count, path, text, EstimateTokens(text)));
            }
        }

        return chunks;
    }

    /// <summary>Chunks pre-sectioned text (a CRM case's symptom / diagnosis / resolution).</summary>
    public static IReadOnlyList<KnowledgeChunk> ChunkSections(
        string title,
        IEnumerable<(string Heading, string Text)> sections,
        ChunkingOptions? options = null)
    {
        options ??= new ChunkingOptions();
        var chunks = new List<KnowledgeChunk>();

        foreach (var (heading, text) in sections)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var path = $"{title} > {heading}";
            foreach (var packed in Pack(Blocks(text.Split('\n')), options))
            {
                chunks.Add(new KnowledgeChunk(chunks.Count, path, packed, EstimateTokens(packed)));
            }
        }

        return chunks;
    }

    private static List<(string Path, List<string> Blocks)> Sections(string title, string markdown)
    {
        var sections = new List<(string Path, List<string> Lines)>();
        var stack = new List<(int Level, string Text)>();
        var current = new List<string>();
        var currentPath = title;
        var inFence = false;

        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            var heading = inFence ? Match.Empty : HeadingRegex().Match(raw);
            if (!heading.Success)
            {
                current.Add(raw);
                continue;
            }

            sections.Add((currentPath, current));
            current = [];

            var level = heading.Groups[1].Value.Length;
            var text = heading.Groups[2].Value.Trim();
            stack.RemoveAll(h => h.Level >= level);
            stack.Add((level, text));

            // An H1 that repeats the title adds nothing to the path.
            var parts = stack.Select(h => h.Text)
                .Where(t => !string.Equals(t, title, StringComparison.OrdinalIgnoreCase));
            currentPath = string.Join(" > ", new[] { title }.Concat(parts));
        }

        sections.Add((currentPath, current));

        return sections
            .Select(s => (s.Path, Blocks(s.Lines)))
            .Where(s => s.Item2.Count > 0)
            .Select(s => (s.Path, s.Item2))
            .ToList();
    }

    /// <summary>Paragraphs, whole lists (blank lines between items included) and whole code fences.</summary>
    private static List<string> Blocks(IEnumerable<string> lines)
    {
        var blocks = new List<string>();
        var current = new List<string>();
        var inList = false;
        var inFence = false;
        var pendingBlank = false;

        void Flush()
        {
            var text = string.Join('\n', current).Trim();
            if (text.Length > 0)
            {
                blocks.Add(text);
            }

            current.Clear();
            inList = false;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (!inFence)
                {
                    Flush();
                }

                current.Add(line);
                inFence = !inFence;
                if (!inFence)
                {
                    Flush();
                }

                continue;
            }

            if (inFence)
            {
                current.Add(line);
                continue;
            }

            if (line.Trim().Length == 0)
            {
                pendingBlank = true;
                if (!inList)
                {
                    Flush();
                }

                continue;
            }

            var isItem = ListItemRegex().IsMatch(line);
            var isContinuation = inList && line.StartsWith("  ", StringComparison.Ordinal);

            if (inList && pendingBlank && !isItem && !isContinuation)
            {
                Flush();
            }
            else if (!inList && isItem)
            {
                Flush();
            }

            if (isItem)
            {
                inList = true;
            }

            pendingBlank = false;
            current.Add(line);
        }

        Flush();
        return blocks;
    }

    private static List<(string Path, List<string> Blocks)> MergeSmall(
        List<(string Path, List<string> Blocks)> sections,
        ChunkingOptions options)
    {
        var merged = new List<(string Path, List<string> Blocks)>();
        (string Path, List<string> Blocks)? carry = null;

        foreach (var section in sections)
        {
            var next = carry is { } c
                ? (c.Path, c.Blocks.Concat([$"{Leaf(section.Path)}:"]).Concat(section.Blocks).ToList())
                : section;

            if (EstimateTokens(string.Join('\n', next.Item2)) < options.MinTokens)
            {
                carry = next;
                continue;
            }

            merged.Add(next);
            carry = null;
        }

        if (carry is { } rest)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                merged[^1] = (last.Path, last.Blocks.Concat(rest.Blocks).ToList());
            }
            else
            {
                merged.Add(rest);
            }
        }

        return merged;
    }

    private static string Leaf(string path) => path.Split(" > ").Last();

    private static IEnumerable<string> Pack(List<string> blocks, ChunkingOptions options)
    {
        var current = new List<string>();
        var currentTokens = 0;
        var overlapBudget = (int)(options.MaxTokens * options.OverlapFraction);

        foreach (var block in blocks.SelectMany(b => SplitOversize(b, options)))
        {
            var tokens = EstimateTokens(block);

            if (current.Count > 0 && currentTokens + tokens > options.MaxTokens)
            {
                yield return string.Join("\n\n", current);

                // Carry the last block forward as overlap when it is short enough, so a step that
                // refers back to the previous paragraph still has it.
                var last = current[^1];
                current.Clear();
                currentTokens = 0;
                if (EstimateTokens(last) <= overlapBudget)
                {
                    current.Add(last);
                    currentTokens = EstimateTokens(last);
                }
            }

            current.Add(block);
            currentTokens += tokens;

            if (currentTokens >= options.TargetTokens && !ListItemRegex().IsMatch(block))
            {
                yield return string.Join("\n\n", current);
                current.Clear();
                currentTokens = 0;
            }
        }

        if (current.Count > 0)
        {
            yield return string.Join("\n\n", current);
        }
    }

    /// <summary>
    /// A paragraph over the maximum is split at sentence boundaries. A list or code block is kept
    /// whole up to twice the maximum (steps stay together), and only then split by item.
    /// </summary>
    private static IEnumerable<string> SplitOversize(string block, ChunkingOptions options)
    {
        var tokens = EstimateTokens(block);
        if (tokens <= options.MaxTokens)
        {
            return [block];
        }

        var isList = ListItemRegex().IsMatch(block);
        var isCode = block.StartsWith("```", StringComparison.Ordinal);
        if ((isList || isCode) && tokens <= options.MaxTokens * 2)
        {
            return [block];
        }

        var pieces = isList
            ? Regex.Split(block, @"\n(?=\s*(?:[-*+]|\d+[.)])\s)")
            : SentenceBoundaryRegex().Split(block);

        var result = new List<string>();
        var builder = new StringBuilder();
        foreach (var piece in pieces)
        {
            if (builder.Length > 0 && EstimateTokens(builder + " " + piece) > options.MaxTokens)
            {
                result.Add(builder.ToString().Trim());
                builder.Clear();
            }

            builder.Append(isList ? "\n" : " ").Append(piece);
        }

        if (builder.Length > 0)
        {
            result.Add(builder.ToString().Trim());
        }

        return result;
    }
}
