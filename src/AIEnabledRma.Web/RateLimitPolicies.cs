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
    /// Every anonymous wizard write: the POST actions for Start, ChooseCustomer, Triage,
    /// Problems, Shipping, Payment and Confirm. They share one bucket, so a caller cannot
    /// multiply its allowance by walking from step to step.
    /// </summary>
    /// <remarks>
    /// All seven are limited, and none is exempt. Each creates or mutates a return, so an
    /// unlimited one is a write amplifier whether or not it costs a model call: Problems
    /// rewrites the descriptions the policy is decided on, Shipping and Payment are the steps
    /// that quote and capture money, and Confirm is the commit. Limiting only Start and Triage
    /// left a caller with one successful Start free to hammer the rest as fast as the socket
    /// allowed.
    ///
    /// The budget is per client address and shared across the steps, which is the point: a
    /// customer walks the wizard in seven posts and stays far inside 30 a minute, including a
    /// retry after a declined card, while a script gets one burst and then nothing.
    /// </remarks>
    public const string WizardWrites = "wizard-writes";
}
