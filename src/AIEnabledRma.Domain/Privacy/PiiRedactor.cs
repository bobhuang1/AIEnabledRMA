using System.Text;
using System.Text.RegularExpressions;

namespace AIEnabledRma.Domain.Privacy;

/// <summary>Kinds of personal data the redactor recognises. The name is the placeholder prefix.</summary>
public enum PiiKind
{
    Email,
    Card,
    Phone,
    Address,
    Order,
    Name,
    Id,
}

/// <summary>
/// Removes personal data from free text before it leaves the process (an AI provider, an
/// embedding service, a knowledge-base index).
/// </summary>
/// <remarks>
/// Pattern recognisers cover email, payment card (Luhn-checked), phone, street address, order or
/// ticket numbers and serial-like identifiers, plus names the caller already knows (the matched
/// customer, a CRM case's agent). Values the caller marks as protected, such as the device serial
/// and SKU the policy needs, are left untouched. Replacements are stable within one
/// <see cref="RedactionSession"/>: the same email becomes the same <c>[EMAIL_1]</c> in every turn
/// of a conversation. Recognition is deliberately greedy: a false positive costs a word of
/// context, a false negative sends a customer's data to a third party.
/// </remarks>
public static partial class PiiRedactor
{
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    // 13-19 digits, optionally grouped by spaces or dashes. Confirmed with Luhn before replacing.
    [GeneratedRegex(@"(?<![\w-])\d(?:[ -]?\d){12,18}(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex CardRegex();

    // A run that starts with + or a digit or "(", made of digits and phone punctuation, 7-15 digits.
    [GeneratedRegex(@"(?<![\w-])(?:\+|\()?\d[\d\s().\-]{5,}\d(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(
        @"\b\d{1,6}\s+(?:[A-Z][A-Za-z'\-]*\.?\s+){1,4}(?:Street|St|Avenue|Ave|Road|Rd|Lane|Ln|Boulevard|Blvd|Drive|Dr|Way|Court|Ct|Place|Pl|Terrace|Parkway|Pkwy|Highway|Hwy|Square|Sq|Close|Crescent)\b\.?(?:,?\s*(?:Apt|Apartment|Suite|Unit|Flat|#)\s*[\w-]+)?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AddressRegex();

    [GeneratedRegex(
        @"\b(?:order|invoice|ticket|case|account|acct|po|reference|ref)\s*(?:no\.?|number|num|#|id)?\s*[:#]?\s*(?=[A-Za-z0-9\-]*\d)[A-Za-z0-9][A-Za-z0-9\-]{3,}",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OrderRegex();

    // Self-introductions and sign-offs: "my name is Jane Smith", "Regards, Jane". Phrases such as
    // "I am" are left out on purpose: "I am Unable to boot" is far more common than a name.
    [GeneratedRegex(
        @"(?:\b(?:[Mm]y name is|[Nn]ame's|[Nn]ame:)\s+|\b(?:[Rr]egards|[Tt]hanks|[Cc]heers|[Ss]incerely)[,!]?[ \t]*\r?\n?[ \t]*-?[ \t]*)(?<name>[A-Z][a-z'\-]+(?:[ \t]+[A-Z][a-z'\-]+){0,2})",
        RegexOptions.CultureInvariant)]
    private static partial Regex IntroducedNameRegex();

    // Serial-like: groups of letters and digits joined by dashes, with at least one digit.
    [GeneratedRegex(@"(?<![\w-])(?=[A-Za-z0-9\-]*\d)[A-Za-z0-9]{2,}(?:-[A-Za-z0-9]{3,}){2,}(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex SerialLikeRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDateRegex();

    // Placeholders we produce, so a second pass (output scan) does not treat them as data.
    [GeneratedRegex(@"\[(?:EMAIL|CARD|PHONE|ADDRESS|ORDER|NAME|ID)_\d+\]", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    /// <summary>Redacts <paramref name="text"/> within <paramref name="session"/>.</summary>
    public static string Redact(string? text, RedactionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // Protected values (device serials, SKUs) are swapped for private-use sentinels first, so
        // no recogniser can touch them, and restored at the end.
        var (shielded, restore) = Shield(text, session.ProtectedValues);

        var result = shielded;
        result = Replace(result, EmailRegex(), PiiKind.Email, session);
        result = Replace(result, CardRegex(), PiiKind.Card, session, m => Luhn(m.Value));
        result = Replace(result, AddressRegex(), PiiKind.Address, session);
        result = Replace(result, OrderRegex(), PiiKind.Order, session);
        result = Replace(result, PhoneRegex(), PiiKind.Phone, session, m => DigitCount(m.Value) is >= 7 and <= 15 && !IsoDateRegex().IsMatch(m.Value));
        result = Replace(result, SerialLikeRegex(), PiiKind.Id, session);
        result = ReplaceIntroducedNames(result, session);
        result = ReplaceKnownNames(result, session);

        return restore(result);
    }

    /// <summary>
    /// Finds personal data in text that should contain none (a model's reply). Returns the kinds
    /// found; placeholders and protected values are ignored.
    /// </summary>
    public static IReadOnlyList<PiiKind> Detect(string? text, RedactionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var probe = new RedactionSession(session.ProtectedValues, session.KnownNames);
        Redact(PlaceholderRegex().Replace(text, " "), probe);
        return probe.Counts.Where(c => c.Value > 0).Select(c => c.Key).ToList();
    }

    private static string Replace(
        string input,
        Regex regex,
        PiiKind kind,
        RedactionSession session,
        Func<Match, bool>? accept = null) =>
        regex.Replace(input, m => accept is null || accept(m) ? session.PlaceholderFor(kind, m.Value) : m.Value);

    private static string ReplaceIntroducedNames(string input, RedactionSession session) =>
        IntroducedNameRegex().Replace(input, m =>
        {
            var name = m.Groups["name"];
            var prefix = m.Value[..(name.Index - m.Index)];
            return prefix + session.PlaceholderFor(PiiKind.Name, name.Value);
        });

    private static string ReplaceKnownNames(string input, RedactionSession session)
    {
        var result = input;

        // Longest first, so "Jane Smith" is replaced as one name before "Jane" alone.
        foreach (var name in session.KnownNames.OrderByDescending(n => n.Length))
        {
            var pattern = $@"(?<![\w]){Regex.Escape(name)}(?![\w])";
            result = Regex.Replace(
                result,
                pattern,
                m => session.PlaceholderFor(PiiKind.Name, m.Value),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }

        return result;
    }

    private static (string Text, Func<string, string> Restore) Shield(string text, IReadOnlyCollection<string> protectedValues)
    {
        if (protectedValues.Count == 0)
        {
            return (text, s => s);
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = text;
        var i = 0;

        foreach (var value in protectedValues.Where(v => v.Length >= 3).OrderByDescending(v => v.Length))
        {
            var sentinel = $"{i++}";
            var replaced = Regex.Replace(
                result,
                Regex.Escape(value),
                m =>
                {
                    map[sentinel] = m.Value;
                    return sentinel;
                },
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            result = replaced;
        }

        return (result, s =>
        {
            var restored = new StringBuilder(s);
            foreach (var (sentinel, original) in map)
            {
                restored.Replace(sentinel, original);
            }

            return restored.ToString();
        });
    }

    private static int DigitCount(string value) => value.Count(char.IsAsciiDigit);

    /// <summary>Luhn checksum over the digits of <paramref name="value"/>.</summary>
    public static bool Luhn(string value)
    {
        var digits = value.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }
}

/// <summary>
/// Placeholder state for one conversation or one document: the same value always maps to the
/// same placeholder. Holds the original values in memory only; never log or persist it.
/// </summary>
public sealed class RedactionSession
{
    private readonly Dictionary<(PiiKind, string), string> _placeholders = new();
    private readonly Dictionary<PiiKind, int> _counts = Enum.GetValues<PiiKind>().ToDictionary(k => k, _ => 0);

    public RedactionSession(IEnumerable<string>? protectedValues = null, IEnumerable<string>? knownNames = null)
    {
        ProtectedValues = (protectedValues ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Single names shorter than 3 letters would match ordinary words ("Al", "Jo").
        KnownNames = (knownNames ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Where(n => n.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Values that must pass through unchanged (device serial, SKU, model).</summary>
    public IReadOnlyCollection<string> ProtectedValues { get; }

    /// <summary>Names known from context (the matched customer, a case's agents).</summary>
    public IReadOnlyCollection<string> KnownNames { get; }

    /// <summary>Distinct values replaced, per kind. Safe to log: counts only.</summary>
    public IReadOnlyDictionary<PiiKind, int> Counts => _counts;

    public int Total => _counts.Values.Sum();

    internal string PlaceholderFor(PiiKind kind, string value)
    {
        var key = (kind, Normalise(kind, value));
        if (!_placeholders.TryGetValue(key, out var placeholder))
        {
            _counts[kind]++;
            placeholder = $"[{kind.ToString().ToUpperInvariant()}_{_counts[kind]}]";
            _placeholders[key] = placeholder;
        }

        return placeholder;
    }

    private static string Normalise(PiiKind kind, string value) => kind switch
    {
        PiiKind.Phone or PiiKind.Card => new string(value.Where(char.IsAsciiDigit).ToArray()),
        _ => value.Trim().ToUpperInvariant(),
    };
}
