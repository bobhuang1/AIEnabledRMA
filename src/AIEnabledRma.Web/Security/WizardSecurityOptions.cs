namespace AIEnabledRma.Web.Security;

/// <summary>Limits on the anonymous wizard (F5, D6: it stays anonymous, so it gets expiry and rate limits).</summary>
public sealed class WizardSecurityOptions
{
    public const string SectionName = "WizardSecurity";

    /// <summary>
    /// Days an unconfirmed request's link (its GUID capability) keeps working. After that the
    /// steps refuse it and the customer starts again.
    /// </summary>
    public int CapabilityLifetimeDays { get; set; } = 14;

    /// <summary>Wizard POSTs one client address may make per minute.</summary>
    public int PostsPerMinutePerClient { get; set; } = 30;

    /// <summary>Wizard POSTs that may address one request id per minute, from anywhere.</summary>
    public int PostsPerMinutePerRequest { get; set; } = 20;
}

/// <summary>Operator-only surfaces (until the operator view brings real sign-in).</summary>
public sealed class OperationsOptions
{
    public const string SectionName = "Operations";

    /// <summary>
    /// Shared key for <c>/health/details</c>, sent as <c>X-Operator-Key</c>. Supply it from the
    /// environment or a secret store. When it is empty the details are only served in Development.
    /// </summary>
    public string? ApiKey { get; set; }
}

/// <summary>Payment provider selection.</summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary><c>fake</c> (default) or <c>stripe</c> (plan D5).</summary>
    public string Provider { get; set; } = "fake";

    /// <summary>
    /// Allows the accept-all fake gateway outside Development and Testing. Only for a demo
    /// deployment that is understood to take no real money.
    /// </summary>
    public bool AllowFakeGateway { get; set; }
}
