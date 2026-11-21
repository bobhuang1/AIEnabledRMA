namespace AIEnabledRma.Web;

/// <summary>Settings for the anonymous wizard's capability URLs.</summary>
public sealed class WizardOptions
{
    public const string SectionName = "Wizard";

    /// <summary>
    /// How long an unconfirmed request stays usable. The request GUID is the capability: anyone
    /// holding it can drive that return, which is the accepted trade for a wizard with no
    /// sign-in. That acceptance assumes the link stops working, so an abandoned draft expires.
    /// </summary>
    public int CapabilityExpiryDays { get; set; } = 30;
}
