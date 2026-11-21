namespace AIEnabledRma.Domain.Configuration;

/// <summary>
/// The secret values this repository ships as examples, and the refusal a Production host reports
/// when it finds one.
/// </summary>
/// <remarks>
/// <c>deploy/prod.env.example</c> is copied into a deployment and filled in, so every value it
/// carries is a placeholder by design: <c>replace-me</c>, <c>replace-with-a-long-random-key</c>,
/// <c>sk_live_replace-me</c>. An operator who copies the file and stops there gets a deployment
/// that starts and looks configured — the secret is present, non-empty, and wrong. The rules that
/// already refuse a missing secret cannot see the difference, so the placeholder is refused here
/// by name.
/// </remarks>
public static class ProductionSecrets
{
    // Fragments of the shipped examples. Compared case-insensitively because the same placeholder
    // is spelled `Replace-Me` in some secret stores, and a rule that only knew the lower-case form
    // would pass the value it exists to catch.
    private static readonly string[] PlaceholderMarkers =
    [
        "replace-me",
        "replace-with-",
        "replace_me",
        "change-me",
        "changeme",
        "your-key",
        "your-secret",
        "placeholder",
    ];

    /// <summary>Whether this value is one of the examples rather than a secret somebody chose.</summary>
    public static bool IsPlaceholder(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && PlaceholderMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The problem a host reports for a placeholder, naming the setting so the operator does not
    /// have to guess which of several secrets is still the example.
    /// </summary>
    public static string Problem(string configurationKey) =>
        $"{configurationKey} is still a placeholder from the example settings. Set it from the deployment's secret store.";

    /// <summary>Whether the value is missing or a placeholder: both are unusable as a secret.</summary>
    public static bool IsUnusable(string? value) =>
        string.IsNullOrWhiteSpace(value) || IsPlaceholder(value);
}
