namespace AIEnabledRma.Domain.Catalog;

/// <summary>
/// Warranty coverage attached to a device. A device can carry more than one
/// (for example a manufacturer warranty plus an extended service plan).
/// </summary>
public sealed class Warranty
{
    public required Guid Id { get; set; }

    public required Guid DeviceId { get; set; }

    public Device? Device { get; set; }

    public required string PlanName { get; set; }

    public required DateOnly StartDate { get; set; }

    public required DateOnly EndDate { get; set; }

    /// <summary>
    /// Free-form policy tier, e.g. "standard", "extended", "premium". Matched against
    /// <see cref="AIEnabledRma.Domain.Rules.IRmaPolicy"/> tiers; adding a tier needs a
    /// policy configuration change, not a code change.
    /// </summary>
    public required string Tier { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }
}
