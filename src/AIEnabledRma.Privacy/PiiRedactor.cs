using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AIEnabledRma.Domain.Abstractions;

namespace AIEnabledRma.Privacy;

/// <summary>
/// Pattern-based PII detection for text that is about to be sent to a model.
/// </summary>
/// <remarks>
/// Detection is deliberately conservative in one direction only: a false positive costs the model
/// a placeholder it can work around, while a false negative hands a customer's address to a
/// provider. Values are replaced, never added to a report; the caller receives counts by kind so
/// it can log "redacted 2 emails" without logging the emails.
///
/// Ordering matters. Email goes first because it contains an <c>@</c> nothing else wants; the card
/// before the phone because a card number is a long run of digits the phone pattern would happily
/// claim; the order-number pattern before the phone for the same reason, so <c>RMA-2026-00001</c>
/// stays one identifier rather than a phone number with a prefix; and the address before the phone
/// so a house number is not read as one.
/// </remarks>
public sealed partial class PiiRedactor : IPiiRedactor
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public IPiiRedactionSession BeginSession(
        IEnumerable<string>? customerNames = null,
        IEnumerable<string>? protectedValues = null) =>
        new Session(customerNames, protectedValues);

    // ---------- recognisers ----------

    [GeneratedRegex(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex Email();

    /// <summary>Digit runs long enough to be a card, with or without grouping separators.</summary>
    [GeneratedRegex(
        @"(?<![\d])(?:\d[ \-]?){12,18}\d(?![\d])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex CardCandidate();

    /// <summary>SSN, national tax id, or any dashed number that is clearly an identifier.</summary>
    [GeneratedRegex(
        @"(?<![\dA-Za-z\-])\d{2,3}-\d{2,7}(?:-\d{2,4})?(?![\dA-Za-z\-])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex DashedIdentifier();

    /// <summary>A house number followed by a street name and a street suffix.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])\d{1,6}[A-Za-z]?\s+(?:[A-Z][A-Za-z'.\-]+\s+){0,4}(?:street|avenue|road|lane|drive|boulevard|court|place|terrace|close|crescent|highway|parkway|square|way|row|walk|st\.?|ave\.?|rd\.?|ln\.?|dr\.?|blvd\.?|ct\.?|pl\.?|ter\.?|hwy\.?|pkwy\.?|sq\.?)(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex NumberedAddress();

    /// <summary>"Harrison Street" with no house number, which is still an address in a sentence.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:[A-Z][A-Za-z'\-]+\s+){1,3}(?:Street|Avenue|Road|Lane|Drive|Boulevard|Court|Place|Terrace|Close|Crescent|Highway|Parkway|Square|Way)(?![A-Za-z])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex StreetOnly();

    /// <summary>Serial-, order- and case-like tokens: letters and digits joined by a separator.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:[A-Z]{2,5}[-–—]?\d{2,8}(?:[-–—]\d{2,8})*|\d{6,12}[-–—][A-Z0-9]{2,8})(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex SerialOrOrder();

    /// <summary>Phone numbers in the shapes people write them.</summary>
    [GeneratedRegex(
        @"(?<![\w+])(?:\+\d{1,3}[\s.\-]?)?(?:\(\d{1,4}\)[\s.\-]?)?\d[\d\s.\-()]{5,}\d(?![\w])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex Phone();

    private sealed class Session : IPiiRedactionSession
    {
        private readonly ConcurrentDictionary<(string Kind, string Key), int> _assigned = new();
        private readonly ConcurrentDictionary<string, int> _next = new(StringComparer.Ordinal);
        private readonly IReadOnlyList<string> _names;
        private readonly IReadOnlyList<string> _protected;

        public Session(IEnumerable<string>? customerNames, IEnumerable<string>? protectedValues)
        {
            _names = (customerNames ?? [])
                .Select(n => n?.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n) && n!.Length >= 2)
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(n => n.Length)
                .ToList();

            _protected = (protectedValues ?? [])
                .Select(v => v?.Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v) && v!.Length >= 3)
                .Select(v => v!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public PiiRedactionResult Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new PiiRedactionResult(text ?? string.Empty, new Dictionary<string, int>());
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var result = text;

            result = Substitute(result, Email(), "EMAIL", m => m.Value.ToLowerInvariant(), counts);
            result = Substitute(
                result,
                CardCandidate(),
                "CARD",
                m => Digits(m.Value),
                counts,
                m => IsLuhnValid(m.Value) && Digits(m.Value).Length is >= 13 and <= 19);

            // A dashed identifier can be a tax id ("99-1234567") or a social security number; both
            // are identity, and neither is needed to diagnose a fault.
            result = Substitute(result, DashedIdentifier(), "ID", m => Digits(m.Value), counts);

            result = Substitute(result, NumberedAddress(), "ADDRESS", Normalize, counts);
            result = Substitute(result, StreetOnly(), "ADDRESS", Normalize, counts);

            result = Substitute(
                result,
                SerialOrOrder(),
                "IDENTIFIER",
                m => m.Value.ToUpperInvariant(),
                counts,
                m => !IsProtected(m.Value) && HasLetterAndDigit(m.Value));

            result = Substitute(
                result,
                Phone(),
                "PHONE",
                m => Digits(m.Value),
                counts,
                // The serial was protected above, and its digit half looks like a phone number.
                // Without this guard the serial survives the identifier pass and is then eaten
                // by the phone pass, which is the one thing the policy needs to keep.
                m => !IsProtected(m.Value) && IsPhoneShaped(m.Value));

            foreach (var name in _names)
            {
                result = Substitute(
                    result,
                    new Regex(
                        $@"(?<![A-Za-z0-9]){Regex.Escape(name)}(?![A-Za-z0-9])",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        MatchTimeout),
                    "NAME",
                    _ => name.ToLowerInvariant(),
                    counts);
            }

            return new PiiRedactionResult(result, counts);
        }

        public bool ContainsIdentifiers(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            // A probe session: detection must not consume placeholder numbers from the live one.
            var probe = new Session(_names, _protected);
            return !string.Equals(probe.Redact(text).Text, text, StringComparison.Ordinal);
        }

        private string Substitute(
            string text,
            Regex pattern,
            string kind,
            Func<Match, string> key,
            IDictionary<string, int> counts,
            Func<Match, bool>? accept = null) =>
            pattern.Replace(text, match =>
            {
                if (accept is not null && !accept(match))
                {
                    return match.Value;
                }

                var value = key(match);
                if (string.IsNullOrWhiteSpace(value))
                {
                    return match.Value;
                }

                var index = _assigned.GetOrAdd(
                    (kind, value),
                    _ => _next.AddOrUpdate(kind, 1, (_, current) => current + 1));

                counts[kind] = counts.TryGetValue(kind, out var seen) ? seen + 1 : 1;
                return $"[{kind}_{index}]";
            });

        private bool IsProtected(string value) =>
            _protected.Any(p =>
                string.Equals(p, value.Trim(), StringComparison.OrdinalIgnoreCase)
                // Either direction: a fragment of the serial must be kept, and so must a value
                // that merely contains it.
                || value.Contains(p, StringComparison.OrdinalIgnoreCase)
                || p.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Luhn check digit. Without it, every long number in a fault description — a firmware
        /// build, a batch code — would be redacted as a card and the model would lose the detail
        /// it needs; with it, the false positives are rare enough to accept the false negatives.
        /// </summary>
        private static bool IsLuhnValid(string candidate)
        {
            var digits = Digits(candidate);
            if (digits.Length < 13)
            {
                return false;
            }

            var sum = 0;
            var doubleIt = false;
            for (var i = digits.Length - 1; i >= 0; i--)
            {
                var digit = digits[i] - '0';
                if (doubleIt)
                {
                    digit *= 2;
                    if (digit > 9)
                    {
                        digit -= 9;
                    }
                }

                sum += digit;
                doubleIt = !doubleIt;
            }

            return sum % 10 == 0;
        }

        private static bool IsPhoneShaped(string raw)
        {
            var digits = Digits(raw);
            if (digits.Length is < 7 or > 15)
            {
                return false;
            }

            // A date or a time is not a phone number, and redacting them loses the fault date the
            // customer was describing.
            var trimmed = raw.Trim();
            if (trimmed.Contains(':')
                || DateOnlyRegex().IsMatch(trimmed)
                || YearOnlyRegex().IsMatch(trimmed))
            {
                return false;
            }

            return true;
        }

        private static bool HasLetterAndDigit(string value) =>
            value.Any(char.IsAsciiLetter) && value.Any(char.IsAsciiDigit);

        private static string Normalize(Match match) =>
            string.Join(' ', match.Value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToLowerInvariant();

        private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());
    }

    [GeneratedRegex(@"^\d{4}[-/.]\d{1,2}[-/.]\d{1,2}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex DateOnlyRegex();

    [GeneratedRegex(@"^(19|20)\d{2}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex YearOnlyRegex();
}
