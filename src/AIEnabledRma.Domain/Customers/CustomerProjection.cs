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
    /// Whether a typed value identifies the customer exactly: the whole email, or every digit of
    /// the phone number. Anything weaker is a guess dressed as a confirmation.
    /// </summary>
    public static bool IsExactContactMatch(string? typed, Customer customer)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return false;
        }

        var value = typed.Trim();
        if (!string.IsNullOrEmpty(customer.Email)
            && string.Equals(value, customer.Email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var typedDigits = Digits(value);
        var phoneDigits = Digits(customer.PhoneNumber ?? string.Empty);
        return typedDigits.Length >= 7 && typedDigits == phoneDigits;
    }

    /// <summary>
    /// Applies <see cref="IsExactContactMatch"/> to the channels the caller actually supplied.
    /// Null means "not supplied", not "must not match".
    /// </summary>
    public static bool MatchesExactContact(Customer customer, string? email, string? phoneNumber) =>
        (email is null || IsExactContactMatch(email, customer))
        && (phoneNumber is null || IsExactContactMatch(phoneNumber, customer));

    public static string MaskName(string? first, string? last)
    {
        var f = string.IsNullOrWhiteSpace(first) ? "?" : first.Trim();
        var l = string.IsNullOrWhiteSpace(last) ? string.Empty : $" {last.Trim()[0]}.";
        return f + l;
    }

    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.IndexOf('@') is var at && at <= 0)
        {
            return null;
        }

        var domain = email[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        var maskedDomain = dot > 0 ? $"{domain[0]}•••{domain[dot..]}" : "•••";
        return $"{email[0]}•••@{maskedDomain}";
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
