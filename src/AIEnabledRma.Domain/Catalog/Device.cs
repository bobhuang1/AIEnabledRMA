namespace AIEnabledRma.Domain.Catalog;

/// <summary>
/// A physical unit that was sold to a customer and can be returned.
/// </summary>
public sealed class Device
{
    public required Guid Id { get; set; }

    public required Guid ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>Vendor serial number. The system of record for the RMA process.</summary>
    public required string SerialNumber { get; set; }

    public string? MacAddress { get; set; }

    public string? Imei { get; set; }

    /// <summary>Additional identifiers (partner serials, asset tags) for fuzzy lookup.</summary>
    public ICollection<DeviceAlternateIdentifier> AlternateIdentifiers { get; set; } =
        new List<DeviceAlternateIdentifier>();

    public string? HardwareRevision { get; set; }

    public string? FirmwareVersion { get; set; }

    /// <summary>Serial of the unit the customer received as a prior replacement, if any.</summary>
    public string? ReplacementForSerialNumber { get; set; }

    public DateTimeOffset? ShippedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public ICollection<Warranty> Warranties { get; set; } = new List<Warranty>();
}

/// <summary>
/// An extra identifier that also resolves to a device, for cross-system matching.
/// </summary>
public sealed class DeviceAlternateIdentifier
{
    public required Guid Id { get; set; }

    /// <summary>Set by the ORM from the <see cref="Device"/> navigation.</summary>
    public Guid DeviceId { get; set; }

    public Device? Device { get; set; }

    public required string Identifier { get; set; }

    public required DeviceIdentifierKind Kind { get; set; }
}
