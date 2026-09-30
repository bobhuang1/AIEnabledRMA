using AIEnabledRma.Rag.Knowledge;

namespace AIEnabledRma.Rag;

/// <summary>
/// Deterministic, inspectable rule layer that runs before any model call.
///
/// Its job is to stop the model from being asked questions it must not answer, and to reduce
/// the number of cases that reach the model at all. It is ordinary C#: no prompt, no
/// model, nothing to fail.
/// </summary>
public static class PreTriageRules
{
    /// <summary>
    /// Blocks a product family that the deployment has not opted in to. Being off by default
    /// means a new article or a new product family is excluded until someone deliberately
    /// enables it, which is the direction you want for a safety-related system.
    /// </summary>
    public static bool IsProductFamilyEnabled(
        IReadOnlySet<string> enabledProductFamilies,
        string productFamily) =>
        !string.IsNullOrWhiteSpace(productFamily)
        && enabledProductFamilies.Contains(productFamily);

    /// <summary>
    /// True when the identifier is well enough formed for a device lookup. Length and
    /// character checks only — deliberately no regex per vendor, so a new identifier format
    /// never needs a code change to be recognised as "not obviously wrong".
    /// </summary>
    public static bool LooksLikeDeviceIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        var trimmed = identifier.Trim();
        return trimmed.Length is >= 4 and <= 64;
    }

    /// <summary>
    /// Collects the scope tokens the retriever and the scope guard both use. A session with no
    /// recognised product family yields an empty set, which fails closed everywhere: the
    /// retriever returns only universal articles and the model sees no product-specific
    /// guidance.
    /// </summary>
    public static IReadOnlySet<string> BuildScopeTokens(
        string? productFamily,
        IReadOnlyCollection<string>? productTokens)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(productFamily))
        {
            tokens.Add(productFamily.Trim());
        }

        if (productTokens is not null)
        {
            foreach (var token in productTokens.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                tokens.Add(token.Trim());
            }
        }

        return tokens;
    }

    /// <summary>
    /// Converts the free-text product field on an RMA into the tokens used for scoping.
    /// </summary>
    public static IReadOnlySet<string> ScopeTokensForRma(string? product)
    {
        if (string.IsNullOrWhiteSpace(product))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var parts = product
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('-', '–', ',', '/'))
            .Where(p => p.Length > 1);

        return new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects a request the assistant must decline regardless of what the knowledge base
    /// says. Returns the reason when the message should be refused, or null to proceed.
    ///
    /// This exists because a prompt instruction is not a control. A model asked to help with
    /// something the knowledge base does not cover will sometimes answer anyway; refusing the
    /// request before it is ever phrased to a model is the only way to make the limit real.
    /// </summary>
    public static string? DetectOutOfScopeRequest(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Your message was empty. Please describe the problem with the device.";
        }

        if (message.Length > RagOptions.MaxUserMessageCharacters)
        {
            return $"Please keep your description under {RagOptions.MaxUserMessageCharacters:N0} characters.";
        }

        // The assistant may describe troubleshooting steps. It must not act as one: no remote
        // access, no data erasure, no firmware or bios changes, no opening the enclosure.
        string[] prohibited =
        [
            "remote access", "backdoor", "bypass the warranty", "void the warranty",
            "warranty fraud", "refund without", "skip the", "ignore the policy",
            "wipe the", "erase all data", "reset the bios", "flash the bios",
            "open the enclosure", "remove the battery", "crack the case",
        ];

        var lowered = message.ToLowerInvariant();

        foreach (var phrase in prohibited)
        {
            if (lowered.Contains(phrase, StringComparison.Ordinal))
            {
                return "That request is outside what the returns assistant can help with. "
                    + "I can explain troubleshooting steps and help you start a return, "
                    + "but I cannot override warranty terms or advise on internal device work.";
            }
        }

        return null;
    }
}

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>Longest customer message accepted. Bounds the prompt and the log line.</summary>
    public const int MaxUserMessageCharacters = 4000;

    /// <summary>Root of the knowledge-base directory. Defaults to the folder copied next to the binary.</summary>
    public string KnowledgeBasePath { get; set; } = "knowledge-base";

    /// <summary>
    /// Product families this deployment will give product-specific guidance for. Empty by
    /// default, which means only universal articles are ever retrieved.
    /// </summary>
    public IList<string> EnabledProductFamilies { get; set; } = [];

    /// <summary>Articles handed to the model per request.</summary>
    public int TopK { get; set; } = 3;
}
