using Microsoft.Extensions.Configuration;

namespace AIEnabledRma.Domain.Configuration;

/// <summary>
/// Startup checks for a Production deployment (P11). Each host calls <see cref="Problems"/> with the
/// checks that apply to it and refuses to start when any fail, so a misconfiguration is an obvious
/// crash at deploy time rather than a quiet security gap in service.
/// </summary>
/// <remarks>
/// This lives beside <see cref="ProductionDatabaseCredentials"/> and <see cref="ProductionSecrets"/>
/// rather than in the data layer, because the rules are configuration, not persistence. The RAG host
/// applies the database rule and opens PostgreSQL through Npgsql directly; a copy of it taken from the
/// EF Core project would put an ORM into an image that never uses one, and a copy kept in the RAG
/// project would be the drift this shared implementation exists to prevent.
/// </remarks>
public static class ProductionConfiguration
{
    /// <summary>The bundled offline model's name; see <c>AddAiProvider</c>.</summary>
    public const string ExampleLocalProvider = "example-local";

    /// <summary>The adapter that talks to a real model endpoint.</summary>
    public const string OpenAiCompatibleProvider = "openai-compatible";

    /// <param name="checkOperatorSignIn">
    /// Whether this host serves the operator back office, which needs real sign-in.
    /// </param>
    /// <param name="requireDatabase">Whether this host must have a database at all.</param>
    /// <param name="checkAiProvider">
    /// Whether this host selects a chat model. The tool server does not: its tools evaluate stored
    /// data and policy, so it has no <c>Ai</c> section to be wrong about, and applying the rule to it
    /// would refuse a valid Production deployment for a provider it never calls.
    /// </param>
    public static IReadOnlyList<string> Problems(
        IConfiguration configuration,
        bool checkOperatorSignIn = false,
        bool requireDatabase = true,
        bool checkAiProvider = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var problems = new List<string>(DatabaseProblems(configuration, requireDatabase));

        // The bundled model answers from canned text and cites only what retrieval handed it. It is
        // the right default for a checkout and the wrong model for a customer: a Production
        // deployment that never chose a provider would look configured, answer every question, and
        // never actually ask a model. Blank is refused for the same reason — the provider switch
        // treats an absent setting as 'example-local', so leaving it out is the same decision.
        var provider = configuration["Ai:Provider"];
        if (checkAiProvider
            && (string.IsNullOrWhiteSpace(provider)
                || string.Equals(provider.Trim(), ExampleLocalProvider, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add(
                $"Ai:Provider is '{ExampleLocalProvider}', the bundled example model. Production needs a real provider, "
                + $"for example '{OpenAiCompatibleProvider}' with Ai:OpenAiCompatible:BaseUrl set.");
        }

        RequireHttps(configuration, "Ai:Provider", OpenAiCompatibleProvider, "Ai:OpenAiCompatible:BaseUrl", problems);
        RequireHttps(configuration, "Embeddings:Provider", OpenAiCompatibleProvider, "Embeddings:BaseUrl", problems);

        if (checkOperatorSignIn)
        {
            if (string.IsNullOrWhiteSpace(configuration["OperatorAuth:Authority"])
                || string.IsNullOrWhiteSpace(configuration["OperatorAuth:ClientId"]))
            {
                problems.Add("OperatorAuth:Authority and OperatorAuth:ClientId are required: the operator area needs real sign-in.");
            }

            // A confidential client's secret is optional here — a public client with PKCE has none —
            // but the example's placeholder is not a secret, and a deployment that kept it would
            // fail the code exchange instead of refusing to start.
            if (ProductionSecrets.IsPlaceholder(configuration["OperatorAuth:ClientSecret"]))
            {
                problems.Add(ProductionSecrets.Problem("OperatorAuth:ClientSecret"));
            }
        }

        if (string.Equals(configuration["Payments:Provider"], "stripe", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var key in new[] { "Payments:Stripe:SecretKey", "Payments:Stripe:WebhookSecret" })
            {
                if (ProductionSecrets.IsPlaceholder(configuration[key]))
                {
                    problems.Add(ProductionSecrets.Problem(key));
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// The database half of the rule, on its own. The RAG host is valid without a database — its
    /// lexical index reads the corpus and nothing else — so it applies this, with
    /// <paramref name="requireDatabase"/> saying whether a store is configured, without also
    /// acquiring a model-provider rule that does not apply to it.
    /// </summary>
    public static IReadOnlyList<string> DatabaseProblems(IConfiguration configuration, bool requireDatabase = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connection = configuration.GetConnectionString("Rma");
        if (string.IsNullOrWhiteSpace(connection))
        {
            return requireDatabase ? ["ConnectionStrings:Rma is not set."] : [];
        }

        return ProductionDatabaseCredentials.ProblemWith(connection) is { } problem ? [problem] : [];
    }

    /// <summary>Throws with every problem listed when the configuration is unsafe for Production.</summary>
    public static void EnsureSafe(
        IConfiguration configuration,
        bool checkOperatorSignIn = false,
        bool requireDatabase = true,
        bool checkAiProvider = true)
    {
        var problems = Problems(configuration, checkOperatorSignIn, requireDatabase, checkAiProvider);
        if (problems.Count > 0)
        {
            throw ProductionConfigurationGuard.Refusal(problems.ToArray());
        }
    }

    private static void RequireHttps(IConfiguration configuration, string providerKey, string provider, string urlKey, List<string> problems)
    {
        if (!string.Equals(configuration[providerKey], provider, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!Uri.TryCreate(configuration[urlKey], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add($"{urlKey} must be an https URL in Production.");
        }
    }
}
