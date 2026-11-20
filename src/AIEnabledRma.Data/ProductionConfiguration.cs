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
    /// <summary>Passwords that appear in this repository's development and CI settings.</summary>
    private static readonly HashSet<string> KnownDefaultPasswords = new(StringComparer.Ordinal)
    {
        "", "rmapassword", "placeholder", "postgres", "password", "ci-only-password", "testonly", "changeme",
    };

    public static IReadOnlyList<string> Problems(IConfiguration configuration, bool checkOperatorSignIn = false, bool checkRagKeys = false)
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
                if (KnownDefaultPasswords.Contains(builder.Password ?? string.Empty))
                {
                    problems.Add("ConnectionStrings:Rma uses a default or empty password.");
                }
            }
            catch (ArgumentException)
            {
                problems.Add("ConnectionStrings:Rma is not a valid PostgreSQL connection string.");
            }
        }

        RequireHttps(configuration, "Ai:Provider", "openai-compatible", "Ai:OpenAiCompatible:BaseUrl", problems);
        RequireHttps(configuration, "Embeddings:Provider", "openai-compatible", "Embeddings:BaseUrl", problems);

        if (configuration.GetValue<bool>("Payments:AllowFakeGateway"))
        {
            problems.Add("Payments:AllowFakeGateway is on: the fake gateway accepts every payment.");
        }

        if (checkOperatorSignIn
            && (string.IsNullOrWhiteSpace(configuration["OperatorAuth:Authority"]) || string.IsNullOrWhiteSpace(configuration["OperatorAuth:ClientId"])))
        {
            problems.Add("OperatorAuth:Authority and OperatorAuth:ClientId are required: the operator area needs real sign-in.");
        }

        if (checkRagKeys
            && (string.IsNullOrWhiteSpace(configuration["Rag:Security:AdminKey"]) || string.IsNullOrWhiteSpace(configuration["Rag:Security:ReaderKey"])))
        {
            problems.Add("Rag:Security:AdminKey and ReaderKey are required.");
        }

        return problems;
    }

    /// <summary>Throws with every problem listed when the configuration is unsafe for Production.</summary>
    public static void EnsureSafe(IConfiguration configuration, bool checkOperatorSignIn = false, bool checkRagKeys = false)
    {
        var problems = Problems(configuration, checkOperatorSignIn, checkRagKeys);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Refusing to start in Production with an unsafe configuration:\n  - " + string.Join("\n  - ", problems));
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
