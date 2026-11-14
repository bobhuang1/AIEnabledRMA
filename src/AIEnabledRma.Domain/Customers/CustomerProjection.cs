namespace AIEnabledRma.Domain.Customers;

/// <summary>
/// The only customer shape that may leave the system on an agent- or customer-facing path.
/// </summary>
/// <remarks>
/// The wizard and the MCP tools ask the same question — "which customer is this, and can they
/// recognise it?" — so they must give the same answer. They did not: the wizard masked names,
/// emails, phones and stored addresses, while the MCP server returned the entity's fields, which
/// handed every agent a full transcript copy of a customer's contact details. Masking lives here,
/// in one place, so a field added to the entity does not arrive in a tool response by default.
///
/// Nothing here decides identity. Exactness is decided by <see cref="MatchesExactContact"/>, which
/// exists because a partial contact lookup is an enumeration oracle: ask for "j•••@e•••.com" a
/// character at a time and the match count tells you the address.
/// </remarks>
public sealed record CustomerProjection
{
    public required Guid Id { get; init; }

    public required string MaskedName { get; init; }

    public string? CompanyName { get; init; }

    public string? MaskedEmail { get; init; }

    public string? MaskedPhone { get; init; }

    public string? PreferredLanguage { get; init; }

    /// <summary>The default (or only) stored address, reduced to something the owner recognises.</summary>
    public string? MaskedAddress { get; init; }

    public int AddressCount { get; init; }

    public static CustomerProjection From(Customer customer)
    {
        var defaultAddress = customer.Addresses
            .OrderByDescending(a => a.IsDefault)
            .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return new CustomerProjection
        {
            Id = customer.Id,
            MaskedName = MaskName(customer.FirstName, customer.LastName),
            CompanyName = customer.CompanyName,
            MaskedEmail = MaskEmail(customer.Email),
            MaskedPhone = MaskPhone(customer.PhoneNumber),
            PreferredLanguage = customer.PreferredLanguage,
            MaskedAddress = defaultAddress is null ? null : MaskAddress(defaultAddress),
            AddressCount = customer.Addresses.Count,
        };
    }

    /// <summary>
    /// Whether one typed value identifies the customer exactly: the whole email, or every digit
    /// of the phone number. This is the wizard's question, where the visitor types one identifier
    /// and may have typed either. Anything weaker is a guess dressed as a confirmation.
    /// </summary>
    public static bool IsExactContactMatch(string? typed, Customer customer) =>
        SameEmail(typed, customer.Email) || SamePhone(typed, customer.PhoneNumber);

    /// <summary>
    /// Whether each channel the caller supplied matches that channel of the record. Null means
    /// "not supplied", not "must not match". An email is never confirmed by phone digits and a
    /// phone number is never confirmed by an address.
    /// </summary>
    public static bool MatchesExactContact(Customer customer, string? email, string? phoneNumber) =>
        (email is null || SameEmail(email, customer.Email))
        && (phoneNumber is null || SamePhone(phoneNumber, customer.PhoneNumber));

    private static bool SameEmail(string? typed, string? email) =>
        !string.IsNullOrWhiteSpace(typed)
        && !string.IsNullOrWhiteSpace(email)
        && string.Equals(typed.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A phone number matches when every digit matches and there are enough of them to be a
    /// number rather than a fragment.
    /// </summary>
    private static bool SamePhone(string? typed, string? phoneNumber)
    {
        var typedDigits = Digits(typed ?? string.Empty);
        return typedDigits.Length >= 7 && typedDigits == Digits(phoneNumber ?? string.Empty);
    }

    public static string MaskName(string? first, string? last)
    {
        var f = string.IsNullOrWhiteSpace(first) ? "?" : first.Trim();
        var l = string.IsNullOrWhiteSpace(last) ? string.Empty : $" {last.Trim()[0]}.";
        return f + l;
    }

    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var trimmed = email.Trim();
        var at = trimmed.IndexOf('@');
        if (at <= 0)
        {
            return null;
        }

        var domain = trimmed[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        var maskedDomain = dot > 0 ? $"{domain[0]}•••{domain[dot..]}" : "•••";
        return $"{trimmed[0]}•••@{maskedDomain}";
    }

    public static string? MaskPhone(string? phone)
    {
        var digits = Digits(phone ?? string.Empty);
        return digits.Length < 4 ? null : $"••• {digits[^2..]}";
    }

    public static string MaskAddress(Address address)
    {
        var postal = string.IsNullOrWhiteSpace(address.PostalCode) ? string.Empty : address.PostalCode.Trim();
        var maskedPostal = postal.Length <= 2 ? "•••" : postal[..2] + new string('•', postal.Length - 2);
        return $"•••, {maskedPostal} {address.CountryCode}".Trim();
    }

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());
}
