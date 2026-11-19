using AIEnabledRma.Rag;
using AIEnabledRma.Rag.Knowledge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AIEnabledRma.Rag;

public static class RagServiceCollectionExtensions
{
    /// <summary>
    /// Registers the knowledge base and its retriever.
    ///
    /// Load failures are recorded rather than thrown: the service still starts and serves
    /// diagnostics, so an operator can see <em>why</em> the corpus is empty instead of
    /// watching the process crash-loop with a stack trace. Retrieval itself stays fail-closed
    /// — an empty or errored corpus returns no articles, which the triage pipeline treats as
    /// "no evidence" and routes to human review.
    /// </summary>
    public static IServiceCollection AddRag(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RagOptions>()
            .Bind(configuration.GetSection(RagOptions.SectionName))
            .ValidateOnStart();

        // The reload audit is registered here so the Web host, which also calls AddRag, cannot end
        // up with a corpus that can be swapped without a record; only the RAG host maps the
        // endpoint, and a deployment can replace the sink without touching the endpoint.
        services.AddSingleton<IRagReloadAudit, LoggingRagReloadAudit>();

        // Every host that serves answers loads this corpus, so every one of them states at startup
        // how many articles it loaded and whether they are the reviewed ones. Registered here rather
        // than in each Program because a host that forgot the call is the failure being guarded
        // against.
        services.AddHostedService<KnowledgeBaseStartupCheck>();

        services.AddOptions<RagRetrievalOptions>()
            .Bind(configuration.GetSection(RagRetrievalOptions.SectionName))
            .Validate(o => o.MaxRelatedExpansions >= 0, "MaxRelatedExpansions cannot be negative.")
            .ValidateOnStart();

        // Limits and provenance come from configuration, with one rule the deployment cannot
        // forget: Production always requires review. An unreviewed article is an unreviewed
        // policy answer, and the environment is a fact about the host, not a preference.
        services.AddSingleton(sp =>
        {
            var options = new KnowledgeBaseOptions();
            configuration.GetSection(KnowledgeBaseOptions.SectionName).Bind(options);

            if (sp.GetService<IHostEnvironment>()?.IsProduction() == true)
            {
                options.RequireReview = true;
            }

            return options;
        });

        services.AddSingleton(sp => new KnowledgeBaseLoader(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KnowledgeBaseLoader>>(),
            Microsoft.Extensions.Options.Options.Create(
                sp.GetRequiredService<KnowledgeBaseOptions>())));

        // Resolved once per process. A reload is a deliberate operation (see ReloadAsync), not
        // a file watcher, so a half-written article can never be picked up mid-save.
        services.AddSingleton<KnowledgeBaseLoadResult>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RagOptions>>().Value;
            var loader = sp.GetRequiredService<KnowledgeBaseLoader>();
            var root = ResolveKnowledgeBaseRoot(options.KnowledgeBasePath);

            return loader.Load(root);
        });

        // The holder owns the mutable reference. KnowledgeIndex is registered as a factory
        // over it so consumers can keep injecting KnowledgeIndex and never hold a stale copy
        // across a reload.
        services.AddSingleton(sp =>
        {
            var loaded = sp.GetRequiredService<KnowledgeBaseLoadResult>();
            return new KnowledgeIndexHolder
            {
                Current = new KnowledgeIndex(
                    loaded,
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RagRetrievalOptions>>(),
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RagOptions>>()),
            };
        });

        services.AddSingleton(sp => sp.GetRequiredService<KnowledgeIndexHolder>().Current);

        // Bridges the retriever seam TriagePipeline depends on. Registered over the holder for
        // the same reason as the index above: a reload must not be shadowed by a captured
        // instance. Which implementation answers is a configuration decision, so the seam is a
        // swap in one registration rather than a rewrite of the pipeline that consumes it.
        services.AddSingleton<Domain.Abstractions.IKnowledgeRetriever>(sp =>
        {
            var holder = sp.GetRequiredService<KnowledgeIndexHolder>();

            return sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RagRetrievalOptions>>()
                .Value.SynonymExpansion
                ? new SynonymExpandingRetriever(holder)
                : new KnowledgeIndexRetriever(holder);
        });

        return services;
    }

    /// <summary>
    /// Rebuilds the index from disk. Atomic from the caller's perspective: the new index is
    /// published in a single reference assignment, and a load that produces errors leaves the
    /// previous index in place.
    /// </summary>
    public static async Task<bool> ReloadAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var loader = services.GetRequiredService<KnowledgeBaseLoader>();
        var options = services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<RagOptions>>()
            .Value;

        var result = await Task.Run(
            () => loader.Load(ResolveKnowledgeBaseRoot(options.KnowledgeBasePath)),
            cancellationToken);

        if (result.Errors.Count > 0)
        {
            // Refuse to publish a broken corpus. Serving a partial knowledge base would let
            // the assistant answer confidently from a subset of the policy.
            return false;
        }

        var holder = services.GetRequiredService<KnowledgeIndexHolder>();
        holder.Current = new KnowledgeIndex(
            result,
            services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RagRetrievalOptions>>(),
            services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RagOptions>>());

        return true;
    }

    /// <summary>
    /// Resolves the knowledge-base root. Falls back to the content root so a project run
    /// under `dotnet run` and a published deployment both find the corpus.
    /// </summary>
    internal static string ResolveKnowledgeBaseRoot(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var besideBinary = Path.Combine(AppContext.BaseDirectory, configuredPath);
        if (Directory.Exists(besideBinary))
        {
            return besideBinary;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), configuredPath);
    }
}

/// <summary>
/// Holds the swappable index. Injected by reference so a reload can republish it without
/// restarting the process.
/// </summary>
public sealed class KnowledgeIndexHolder
{
    public required KnowledgeIndex Current { get; set; }
}
