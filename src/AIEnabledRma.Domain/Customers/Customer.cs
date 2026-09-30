namespace AIEnabledRma.Domain.Customers;

public sealed class Customer
{
    public required Guid Id { get; set; }

    /// <summary>External reference from your own CRM or ERP, if you have one.</summary>
    public string? ExternalCustomerId { get; set; }

    public required string FirstName { get; set; }

    public string? MiddleName { get; set; }

    public required string LastName { get; set; }

    public string? CompanyName { get; set; }

    public required string Email { get; set; }

    public string? SecondaryEmail { get; set; }

    public required string PhoneNumber { get; set; }

    public string? PreferredLanguage { get; set; }

    public string? TaxId { get; set; }

    /// <summary>Opt-in flag honoured by the outbound notification adapters.</summary>
    public bool EmailOptIn { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public ICollection<Address> Addresses { get; set; } = new List<Address>();

    public string FullName => string.Join(' ', new[] { FirstName, MiddleName, LastName }
        .Where(p => !string.IsNullOrWhiteSpace(p)));
}

public sealed class Address
{
    public required Guid Id { get; set; }

    public Guid? CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public string? Label { get; set; }

    public required string Line1 { get; set; }

    public string? Line2 { get; set; }

    public string? Line3 { get; set; }

    public required string City { get; set; }

    /// <summary>State, province, or region. Free text so non-US locales need no special case.</summary>
    public string? StateOrProvince { get; set; }

    public required string PostalCode { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public required string CountryCode { get; set; }

    public string? PhoneNumber { get; set; }

    public bool IsDefault { get; set; }
}
