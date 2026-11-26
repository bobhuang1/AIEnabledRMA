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

    /// <summary>
    /// A word that can be somebody's name: one letter-led word, optionally hyphenated or carrying an
    /// apostrophe, and never a token with a digit in it. Being capitalised is the whole test, which
    /// is why this pass needs a cue in front of it rather than scanning every capital.
    /// </summary>
    private const string NameWord = @"[A-Z][a-z]+(?:[-'][A-Za-z]+)?";

    /// <summary>
    /// "Mr Harrison", "Dr. Ada Lovelace": a title is a person by construction, so the words after it
    /// are a name and no model is needed to decide that. Up to three words, because "Dr Ada Lovelace"
    /// is one person and "Mrs Van Der Berg" is another.
    /// </summary>
    /// <remarks>
    /// The title is replaced with the name rather than kept: leaving "Mr." beside a placeholder tells
    /// the model nothing it needs for a fault, and a title word is not itself an identifier.
    /// </remarks>
    [GeneratedRegex(
        @"(?<![A-Za-z])(?:Mr|Mrs|Ms|Mx|Miss|Dr|Prof|Sir|Dame|Rev)\.?[ 	]+(?<name>"
        + NameWord + @"(?:[ 	]+" + NameWord + @"){0,2})(?![A-Za-z])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex TitledName();

    /// <summary>
    /// "my name is Marcus Webb", "I'm Priya Raman", "this is…": the customer introducing themselves.
    /// The cue is matched without regard to case, because people type "i'm", while the name after it
    /// stays case-sensitive — an introduction to "still" or "urgent" is a sentence, not a person.
    /// </summary>
    [GeneratedRegex(
        @"(?<![A-Za-z])(?i:my name is|my name's|i'm|i’m|i am|this is|call me)[ 	]+(?<name>"
        + NameWord + @"(?:[ 	]+" + NameWord + @"){0,2})(?![A-Za-z])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex IntroducedName();

    /// <summary>
    /// Words that come after a cue without naming anybody. The cue pattern cannot tell "This is
    /// Windows 11" from "This is Marcus Webb", so the first word of the candidate decides: a
    /// capitalised product, weekday, month or ordinary continuation is not a person, and a name pass
    /// that redacted them would cost the model the platform the fault is on.
    /// </summary>
    /// <remarks>
    /// A deny-list is the deterministic way to keep the pass narrow. What it cannot do is recognise a
    /// name that is spelled like a product nobody listed, or one introduced in another language —
    /// that is the part a local NER model would have to do.
    /// </remarks>
    private static readonly HashSet<string> NotAName = new(StringComparer.OrdinalIgnoreCase)
    {
        "Not", "Still", "Also", "Just", "Very", "Sorry", "Afraid", "Confused", "Trying", "Getting",
        "Using", "Going", "Able", "Unable", "Here", "There", "Okay", "Ok", "Yes", "No", "Hi", "Hello",
        "Thanks", "Thank", "Please", "On", "In", "At", "The", "A", "An", "And", "But", "So", "It",
        "This", "That", "My", "Our", "Your", "His", "Her", "Their", "When", "If", "After", "Before",
        "Because", "Since", "While", "Once", "Then", "Now", "Today", "Yesterday", "Tomorrow", "Again",
        "Always", "Never", "Maybe", "Sure", "Fine", "Good", "Bad", "Broken", "Dead", "Dark", "Loud",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
        "January", "February", "March", "April", "May", "June", "July", "August", "September",
        "October", "November", "December",
        "Windows", "Mac", "MacBook", "Linux", "Android", "iPhone", "iPad", "Chrome", "Edge", "Firefox",
        "Excel", "Word", "Outlook", "Teams", "Zoom", "Google", "Amazon", "Samsung", "Apple",
        "Microsoft", "Dell", "Lenovo", "Intel", "NVIDIA", "USB", "HDMI", "Bluetooth", "PDF",
        "Fault", "Error", "Code", "Serial", "Model", "Order", "Unit", "Device", "Product", "Support",
        "Engineer", "Technician", "Customer", "Case", "Ticket", "Address", "Phone", "Email",
        "Invoice", "Warranty",
    };

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

            // The deterministic half of the name problem. A title or an introduction names a person
            // by construction, so these are found without a model; a name typed into a fault
            // description with no cue in front of it is still somebody's name and still needs one.
            result = RedactNamePass(result, TitledName(), counts);
            result = RedactNamePass(result, IntroducedName(), counts);

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

        /// <summary>
        /// Replaces a titled or introduced name with a <c>NAME</c> placeholder, keyed by the name
        /// itself so the same person keeps one placeholder whether they arrived through a title or an
        /// introduction — and so a name the caller also supplied collapses onto the same index.
        /// </summary>
        private string RedactNamePass(string text, Regex pattern, IDictionary<string, int> counts) =>
            Substitute(
                text,
                pattern,
                "NAME",
                match => Collapse(match.Groups["name"].Value).ToLowerInvariant(),
                counts,
                match => LooksLikeAName(match.Groups["name"].Value));

        private bool LooksLikeAName(string name)
        {
            var candidate = Collapse(name);
            if (candidate.Length == 0 || IsProtected(candidate))
            {
                return false;
            }

            var first = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            return !NotAName.Contains(first);
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

        private static string Normalize(Match match) => Collapse(match.Value).ToLowerInvariant();

        private static string Collapse(string value) =>
            string.Join(' ', value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());
    }

    [GeneratedRegex(@"^\d{4}[-/.]\d{1,2}[-/.]\d{1,2}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex DateOnlyRegex();

    [GeneratedRegex(@"^(19|20)\d{2}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex YearOnlyRegex();
}
