namespace AIEnabledRma.Domain.Catalog;

/// <summary>
/// A device or merchandise item as tracked by the product catalog.
/// Generic on purpose: a single shape covers handheld scanners, kiosks, sensors,
/// cables, and any other returned merchandise.
/// </summary>
public sealed class Product
{
    public required Guid Id { get; set; }

    /// <summary>Stock-keeping unit. Unique, case-insensitive.</summary>
    public required string Sku { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Free-form grouping used to scope knowledge-base articles, e.g. "handheld-scanner".
    /// Triage refuses to answer questions about a product family that is not the
    /// family of the device(s) in the current RMA session.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>Model designation, used for KB scoping tokens and fuzzy matching.</summary>
    public string? Model { get; set; }

    public bool IsMerchandise { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public ICollection<Device> Devices { get; set; } = new List<Device>();
}
