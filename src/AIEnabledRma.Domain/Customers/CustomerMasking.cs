namespace AIEnabledRma.Domain.Customers;

/// <summary>
/// The one place customer contact details are masked, shared by the anonymous wizard and the
/// MCP tools so the two surfaces can never disagree about what an unauthenticated reader sees.
/// </summary>
/// <remarks>
/// The masks keep just enough for the customer to recognise their own account ("Jane S.",
/// "j•••@e•••.com", "••• 77", "Springfield, 62•••") and nothing that would let a reader
/// contact or locate them.
/// </remarks>
public static class CustomerMasking
{
    private const char Bullet = '•';
    private static readonly string Bullets = new(Bullet, 3);

    /// <summary>First name and the initial of the last name.</summary>
    public static string MaskName(string? first, string? last)
    {
        var f = string.IsNullOrWhiteSpace(first) ? "?" : first.Trim();
        var l = string.IsNullOrWhiteSpace(last) ? string.Empty : $" {last.Trim()[0]}.";
        return f + l;
    }

    /// <summary>First character of the local part and of the domain, plus the top-level domain.</summary>
    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.IndexOf('@') is var at && at <= 0)
        {
            return null;
        }

        var domain = email[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        var maskedDomain = dot > 0 ? $"{domain[0]}{Bullets}{domain[dot..]}" : Bullets;
        return $"{email[0]}{Bullets}@{maskedDomain}";
    }

    /// <summary>The last two digits only.</summary>
    public static string? MaskPhone(string? phone)
    {
        var digits = DigitsOf(phone);
        return digits.Length < 4 ? null : $"{Bullets} {digits[^2..]}";
    }

    /// <summary>The first two characters of the postal code.</summary>
    public static string MaskPostalCode(string? postalCode)
    {
        var postal = string.IsNullOrWhiteSpace(postalCode) ? string.Empty : postalCode.Trim();
        return postal.Length <= 2 ? Bullets : postal[..2] + new string(Bullet, postal.Length - 2);
    }

    /// <summary>Street lines hidden; postal-code prefix and country only (the wizard's form).</summary>
    public static string MaskAddress(Address address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return $"{Bullets}, {MaskPostalCode(address.PostalCode)} {address.CountryCode}".Trim();
    }

    /// <summary>
    /// True when <paramref name="typed"/> is the customer's exact email (case-insensitive) or the
    /// same phone digits (at least seven). Anything weaker is not proof that the typist knows the
    /// contact detail on file.
    /// </summary>
    public static bool IsExactContactMatch(string? typed, Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);

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

        var typedDigits = DigitsOf(value);
        return typedDigits.Length >= 7 && typedDigits == DigitsOf(customer.PhoneNumber);
    }

    /// <summary>True when the value is an exact email match for the customer.</summary>
    public static bool IsExactEmailMatch(string? typed, Customer customer) =>
        !string.IsNullOrWhiteSpace(typed)
        && !string.IsNullOrEmpty(customer.Email)
        && string.Equals(typed.Trim(), customer.Email.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the value has the same phone digits (at least seven) as the customer.</summary>
    public static bool IsExactPhoneMatch(string? typed, Customer customer)
    {
        var digits = DigitsOf(typed);
        return digits.Length >= 7 && digits == DigitsOf(customer.PhoneNumber);
    }

    public static string DigitsOf(string? value) =>
        new((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
}
