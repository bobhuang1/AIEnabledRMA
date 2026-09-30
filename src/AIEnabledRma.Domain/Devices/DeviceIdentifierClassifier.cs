using System.Text.RegularExpressions;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Catalog;

namespace AIEnabledRma.Domain.Devices;

/// <summary>
/// Classifies a pasted identifier by shape and checksum, then normalises it.
/// Fully deterministic and table-driven through <c>DeviceIdentifierRules</c> options so a new
/// identifier scheme can be added by configuration.
/// </summary>
public sealed partial class DeviceIdentifierClassifier
{
    private readonly DeviceIdentifierRules _rules;

    public DeviceIdentifierClassifier(DeviceIdentifierRules rules) => _rules = rules;

    /// <summary>
    /// Interprets one raw token. Tries each configured rule in order and returns the first
    /// match. A value that matches nothing is reported as
    /// <see cref="DeviceIdentifierKind.Unknown"/> rather than guessed at, so the caller can
    /// ask the customer to re-check instead of querying the database with junk.
    /// </summary>
    public DeviceIdentifierInput Classify(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new DeviceIdentifierInput
            {
                RawValue = rawValue ?? string.Empty,
                Kind = DeviceIdentifierKind.Unknown,
                NormalizedValue = string.Empty,
                PassedFormatValidation = false,
            };
        }

        var trimmed = rawValue.Trim();

        foreach (var rule in _rules.Rules)
        {
            if (!rule.IsActive)
            {
                continue;
            }

            var normalized = Normalize(trimmed, rule.Normalization);

            var lengthOk = rule.MinLength is null
                           || normalized.Length >= rule.MinLength.Value;

            var exactLengthOk = rule.ExactLengths is null
                                || !rule.ExactLengths.Contains(normalized.Length);

            var patternOk = rule.Pattern is null
                            || Regex.IsMatch(normalized, rule.Pattern, RegexOptions.IgnoreCase);

            if (lengthOk && exactLengthOk && patternOk)
            {
                var checksumOk = rule.RequiresLuhn ? PassesLuhn(normalized) : true;

                return new DeviceIdentifierInput
                {
                    RawValue = trimmed,
                    Kind = rule.Kind,
                    NormalizedValue = normalized,
                    PassedFormatValidation = checksumOk,
                };
            }
        }

        return new DeviceIdentifierInput
        {
            RawValue = trimmed,
            Kind = DeviceIdentifierKind.Unknown,
            NormalizedValue = trimmed.ToUpperInvariant(),
            PassedFormatValidation = false,
        };
    }

    /// <summary>Splits a pasted block into individual tokens on whitespace, commas, and semicolons.</summary>
    public static IReadOnlyList<string> SplitMany(string? input) =>
        string.IsNullOrWhiteSpace(input)
            ? []
            : Regex.Split(input.Trim(), @"[\s,;]+", RegexOptions.None)
                .Where(t => t.Length > 0)
                .ToArray();

    /// <summary>
    /// Standard Luhn mod-10 check. Applies to IMEI and to any future identifier that adopts
    /// the same checksum, so support teams can catch transcription errors before a database hit.
    /// </summary>
    public static bool PassesLuhn(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var sum = 0;
        var alternate = false;

        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                return false;
            }

            var n = value[i] - '0';

            if (alternate)
            {
                n *= 2;
                if (n > 9)
                {
                    n -= 9;
                }
            }

            sum += n;
            alternate = !alternate;
        }

        return sum % 10 == 0;
    }

    private static string Normalize(string value, DeviceIdentifierNormalization normalization) =>
        normalization switch
        {
            DeviceIdentifierNormalization.HexOnly =>
                new string(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray()),

            DeviceIdentifierNormalization.DigitsOnly =>
                new string(value.Where(char.IsAsciiDigit).ToArray()),

            DeviceIdentifierNormalization.AlphanumericUpper =>
                new string(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray()),

            _ => value.Trim().ToUpperInvariant(),
        };
}

public enum DeviceIdentifierNormalization
{
    None = 0,
    HexOnly = 1,
    DigitsOnly = 2,
    AlphanumericUpper = 3,
}

public sealed class DeviceIdentifierRules
{
    public List<DeviceIdentifierRule> Rules { get; set; } = [];
}

public sealed class DeviceIdentifierRule
{
    public DeviceIdentifierKind Kind { get; set; }

    public int? MinLength { get; set; }

    public List<int>? ExactLengths { get; set; }

    public string? Pattern { get; set; }

    public bool RequiresLuhn { get; set; }

    public DeviceIdentifierNormalization Normalization { get; set; } =
        DeviceIdentifierNormalization.AlphanumericUpper;

    public bool IsActive { get; set; } = true;
}
