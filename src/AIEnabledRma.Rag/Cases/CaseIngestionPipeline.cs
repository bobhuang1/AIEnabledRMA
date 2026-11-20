using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AIEnabledRma.Domain.Cases;
using AIEnabledRma.Domain.Privacy;
using AIEnabledRma.Rag.Store;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIEnabledRma.Rag.Cases;

/// <summary>Per-case outcome of preparation. Counts and kinds only, never values.</summary>
public sealed record CaseReport
{
    /// <summary>Opaque key derived from the CRM record id; not the case number.</summary>
    public required string CaseKey { get; init; }

    public required string Outcome { get; init; }

    public IReadOnlyDictionary<string, int> Redactions { get; init; } = new Dictionary<string, int>();

    /// <summary>Kinds of identifier still present after redaction (why it was quarantined).</summary>
    public IReadOnlyList<string> Residual { get; init; } = [];
}

public sealed record CaseIngestionReport
{
    public required string Source { get; init; }

    public int Read { get; init; }

    public int NotEligible { get; init; }

    public int Prepared { get; init; }

    public int Quarantined { get; init; }

    public IReadOnlyList<CaseReport> Cases { get; init; } = [];

    public KbIngestReport? Published { get; init; }
}

public static class CaseOutcomes
{
    public const string Prepared = "prepared";
    public const string NotEligible = "not-eligible";
    public const string Quarantined = "quarantined";
}

/// <summary>
/// Turns resolved CRM cases into redacted troubleshooting knowledge (plan P6).
/// </summary>
/// <remarks>
/// For each case: only resolved cases with a resolution are used; e-mail header lines and
/// signature blocks are removed; names from greetings, sign-offs and agent labels are collected
/// and redacted everywhere in the case, together with everything <see cref="PiiRedactor"/>
/// recognises. A verification pass then looks for anything identifying that survived (an
/// address, a long digit run, an "@", a case number). A case that fails it is quarantined, not
/// indexed: the pipeline fails closed. The redacted fields become symptom / diagnosis /
/// resolution sections, chunked and published as <c>crm_case</c> documents whose citation reads
/// "Resolved case (product, month)".
/// </remarks>
public sealed partial class CaseIngestionPipeline(
    PgVectorKnowledgeStore store,
    ChunkingOptions? chunking = null,
    ILogger<CaseIngestionPipeline>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<CaseIngestionPipeline>.Instance;

    public const string WatermarkKey = "case_watermark";

    [GeneratedRegex(@"^\s*(?:from|to|cc|bcc|sent|date|subject|reply-to)\s*:.*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderLineRegex();

    // A signature delimiter ("--" or "-- ") and everything after it up to a blank line.
    [GeneratedRegex(@"^--\s*$[\s\S]*?(?:\n\s*\n|\z)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SignatureRegex();

    [GeneratedRegex(@"^(?:On .{0,80} wrote:|Sent from my .*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedReplyRegex();

    [GeneratedRegex(@"\b(?:Hi|Hello|Dear|Hey|Thanks|Thank you|Regards|Cheers|Sincerely|Best)[,!]?[ \t]+(?<name>[A-Z][a-z'\-]+(?:[ \t]+[A-Z][a-z'\-]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex GreetingNameRegex();

    [GeneratedRegex(@"(?m)\b(?i:agent|technician|engineer|tech|rep|representative|assigned to|owner|caller|contact|customer|spoke (?:to|with)|called)\b[ \t]*[:\-]?[ \t]*(?<name>[A-Z][a-z'\-]+(?:[ \t]+[A-Z][a-z'\-]+)?)")]
    private static partial Regex LabelledNameRegex();

    [GeneratedRegex(@"\d{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongDigitRunRegex();

    [GeneratedRegex(@"\b(?:case|ticket|incident|order|cas|inc)[ \t]*(?:no\.?|number|#|id)?[ \t]*[:#\-]?[ \t]*[A-Z]{0,4}-?\d{3,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResidualReferenceRegex();

    /// <summary>Words that look like names after a greeting but are not.</summary>
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Team", "Support", "Customer", "Sir", "Madam", "All", "There", "Again", "Unit", "Device", "The", "Our", "Your",
        "For", "Very", "Much", "Service", "Desk", "Help", "Helpdesk",
    };

    /// <summary>Prepares one case. Pure: no I/O.</summary>
    public (KbDocumentInput? Document, CaseReport Report) Prepare(SupportCase supportCase, string reviewedBy, DateOnly reviewedAt)
    {
        ArgumentNullException.ThrowIfNull(supportCase);
        var key = CaseKey(supportCase.SourceRecordId);

        if (!supportCase.IsResolvedWithResolution)
        {
            return (null, new CaseReport { CaseKey = key, Outcome = CaseOutcomes.NotEligible });
        }

        var removed = new Dictionary<string, int>(StringComparer.Ordinal);
        string PrePass(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var result = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            result = Strip(result, SignatureRegex(), "Signature", removed);
            result = Strip(result, HeaderLineRegex(), "Header", removed);
            result = Strip(result, QuotedReplyRegex(), "Header", removed);
            return result.Trim();
        }

        var title = PrePass(supportCase.Title);
        var description = PrePass(supportCase.Description);
        var notes = supportCase.Notes.Select(PrePass).Where(n => n.Length > 0).ToList();
        var resolution = PrePass(supportCase.Resolution);
        var all = string.Join("\n", new[] { title, description, resolution }.Concat(notes));

        // Names a case reveals about itself: greeted, signed off, or labelled (agent, caller).
        // They are redacted everywhere in the case, not only where they were found.
        var names = GreetingNameRegex().Matches(all).Concat(LabelledNameRegex().Matches(all))
            .Select(m => m.Groups["name"].Value.Trim())
            .SelectMany(n => new[] { n }.Concat(n.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(n => n.Length >= 3 && !NotNames.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var protectedValues = string.IsNullOrWhiteSpace(supportCase.Product) ? [] : new[] { supportCase.Product };
        var session = new RedactionSession(protectedValues, names);

        var symptom = PiiRedactor.Redact(string.Join("\n\n", new[] { title, description }.Where(t => t.Length > 0)), session);
        var diagnosis = PiiRedactor.Redact(string.Join("\n\n", notes), session);
        var fix = PiiRedactor.Redact(resolution, session);

        var counts = session.Counts.Where(c => c.Value > 0).ToDictionary(c => c.Key.ToString(), c => c.Value, StringComparer.Ordinal);
        foreach (var (kind, count) in removed)
        {
            counts[kind] = count;
        }

        // Verification: anything identifying that survived means the case is not indexed.
        var prepared = string.Join("\n", symptom, diagnosis, fix);
        var residual = PiiRedactor.Detect(prepared, new RedactionSession(protectedValues, names)).Select(k => k.ToString()).ToList();
        var withoutProduct = protectedValues.Aggregate(prepared, (text, value) => text.Replace(value, " ", StringComparison.OrdinalIgnoreCase));
        if (withoutProduct.Contains('@'))
        {
            residual.Add("AtSign");
        }

        if (LongDigitRunRegex().IsMatch(withoutProduct))
        {
            residual.Add("DigitRun");
        }

        if (ResidualReferenceRegex().IsMatch(withoutProduct))
        {
            residual.Add("Reference");
        }

        if (residual.Count > 0)
        {
            return (null, new CaseReport { CaseKey = key, Outcome = CaseOutcomes.Quarantined, Redactions = counts, Residual = residual.Distinct().ToList() });
        }

        var month = (supportCase.ClosedOn ?? supportCase.ModifiedOn).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var product = string.IsNullOrWhiteSpace(supportCase.Product) ? "general" : supportCase.Product.Trim();
        var docTitle = "Resolved case: " + FirstLine(symptom);
        var sections = new List<(string Heading, string Text)>
        {
            ("Symptom", symptom),
            ("Diagnosis", diagnosis),
            ("Resolution", fix),
        };

        var document = new KbDocumentInput
        {
            SourceId = key,
            Kind = KbKinds.CrmCase,
            Title = docTitle,
            Category = "resolved-case",
            ProductScope = string.IsNullOrWhiteSpace(supportCase.Product) ? ["*"] : [product],
            ReviewedBy = reviewedBy,
            ReviewedAt = reviewedAt,
            CitationLabel = $"Resolved case ({product}, {month})",
            ContentHash = Hash(string.Join("\n", product, month, symptom, diagnosis, fix)),
            Chunks = MarkdownChunker.ChunkSections(docTitle, sections, chunking),
        };

        return (document, new CaseReport { CaseKey = key, Outcome = CaseOutcomes.Prepared, Redactions = counts });
    }

    /// <summary>
    /// Reads cases modified since the stored watermark, prepares them and, when
    /// <paramref name="publish"/> is set, merges them into the active <c>crm_case</c> generation.
    /// A dry run (the default) only reports: counts per kind per case and the quarantine list.
    /// </summary>
    public async Task<CaseIngestionReport> RunAsync(
        ICaseSource source,
        bool publish,
        string reviewedBy,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (publish && string.IsNullOrWhiteSpace(reviewedBy))
        {
            throw new ArgumentException("Publishing needs the name of the person who reviewed the dry-run sample.", nameof(reviewedBy));
        }

        var watermarkText = await store.GetMetaAsync(WatermarkKey, cancellationToken);
        DateTimeOffset? watermark = watermarkText is null ? null : DateTimeOffset.Parse(watermarkText, CultureInfo.InvariantCulture);

        var reports = new List<CaseReport>();
        var ready = new Dictionary<string, KbDocumentInput>(StringComparer.Ordinal);
        var withdrawn = new HashSet<string>(StringComparer.Ordinal);
        var newest = watermark;
        var read = 0;

        await foreach (var supportCase in source.ReadModifiedSinceAsync(watermark, cancellationToken))
        {
            read++;
            newest = newest is null || supportCase.ModifiedOn > newest ? supportCase.ModifiedOn : newest;

            var (document, report) = Prepare(supportCase, reviewedBy, today);
            reports.Add(report);

            if (document is null)
            {
                // Reopened or failed verification: if an earlier version is indexed, it comes out.
                withdrawn.Add(report.CaseKey);
            }
            else
            {
                ready[document.SourceId] = document;
            }
        }

        var quarantined = reports.Count(r => r.Outcome == CaseOutcomes.Quarantined);
        _logger.LogInformation(
            "Case ingestion from {Source}: {Read} read, {Prepared} prepared, {Quarantined} quarantined, publish={Publish}.",
            source.Name, read, ready.Count, quarantined, publish);

        KbIngestReport? published = null;
        if (publish)
        {
            var existing = await store.LoadActiveDocumentsAsync(KbKinds.CrmCase, cancellationToken);
            var merged = existing
                .Where(d => !withdrawn.Contains(d.SourceId) && !ready.ContainsKey(d.SourceId))
                .Concat(ready.Values)
                .OrderBy(d => d.SourceId, StringComparer.Ordinal)
                .ToList();

            published = await store.IngestAsync(KbKinds.CrmCase, merged, cancellationToken);
            if (published.Activated && newest is { } stamp)
            {
                await store.SetMetaAsync(WatermarkKey, stamp.ToString("O", CultureInfo.InvariantCulture), cancellationToken);
            }
        }

        return new CaseIngestionReport
        {
            Source = source.Name,
            Read = read,
            NotEligible = reports.Count(r => r.Outcome == CaseOutcomes.NotEligible),
            Prepared = ready.Count,
            Quarantined = quarantined,
            Cases = reports,
            Published = published,
        };
    }

    public static string CaseKey(string sourceRecordId) =>
        "case-" + Hash("case:" + sourceRecordId)[..16];

    private static string Strip(string text, Regex regex, string kind, Dictionary<string, int> counts)
    {
        var found = 0;
        var result = regex.Replace(text, _ =>
        {
            found++;
            return "\n";
        });

        if (found > 0)
        {
            counts[kind] = counts.GetValueOrDefault(kind) + found;
        }

        return result;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "untitled";
        return line.Length <= 120 ? line : line[..120];
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
