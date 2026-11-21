using AIEnabledRma.Domain.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AIEnabledRma.Data;

/// <summary>
/// Startup checks for a Production deployment (P11). Each host calls <see cref="Problems"/> with the
/// checks that apply to it and refuses to start when any fail, so a misconfiguration is an obvious
/// crash at deploy time rather than a quiet security gap in service.
/// </summary>
public static class ProductionConfiguration
{
    public static IReadOnlyList<string> Problems(IConfiguration configuration, bool checkOperatorSignIn = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var problems = new List<string>();

        var connection = configuration.GetConnectionString("Rma");
        if (string.IsNullOrWhiteSpace(connection))
        {
            problems.Add("ConnectionStrings:Rma is not set.");
        }
        else
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(connection);
                // The list is shared with the tool server and the RAG host, which apply the same
                // rule from the same place instead of carrying their own copy of it.
                if (ProductionDatabaseCredentials.IsKnownDefault(builder.Password))
                {
                    problems.Add(ProductionDatabaseCredentials.DefaultPasswordProblem);
                }
            }
            catch (ArgumentException)
            {
                problems.Add(ProductionDatabaseCredentials.MalformedConnectionProblem);
            }
        }

        RequireHttps(configuration, "Ai:Provider", "openai-compatible", "Ai:OpenAiCompatible:BaseUrl", problems);
        RequireHttps(configuration, "Embeddings:Provider", "openai-compatible", "Embeddings:BaseUrl", problems);

        if (checkOperatorSignIn
            && (string.IsNullOrWhiteSpace(configuration["OperatorAuth:Authority"]) || string.IsNullOrWhiteSpace(configuration["OperatorAuth:ClientId"])))
        {
            problems.Add("OperatorAuth:Authority and OperatorAuth:ClientId are required: the operator area needs real sign-in.");
        }


        return problems;
    }

    /// <summary>Throws with every problem listed when the configuration is unsafe for Production.</summary>
    public static void EnsureSafe(IConfiguration configuration, bool checkOperatorSignIn = false)
    {
        var problems = Problems(configuration, checkOperatorSignIn);
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
