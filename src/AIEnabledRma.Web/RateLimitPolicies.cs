namespace AIEnabledRma.Web;

/// <summary>
/// Rate-limit policy names, shared between the host that registers the policies and the
/// controllers that opt endpoints into them.
/// </summary>
/// <remarks>
/// Constants rather than configuration because <c>[EnableRateLimiting]</c> takes a compile-time
/// constant. The limits themselves live with the policy in <c>Program</c>, where the reasoning
/// for the numbers sits next to the numbers.
/// </remarks>
public static class RateLimitPolicies
{
    /// <summary>
    /// The anonymous wizard writes: <c>POST /RmaWizard/Start</c>,
    /// <c>POST /RmaWizard/ChooseCustomer</c> and <c>POST /RmaWizard/Triage</c>. They share one
    /// bucket, so a caller cannot multiply its allowance by walking from step to step.
    /// </summary>
    public const string WizardWrites = "wizard-writes";
}
