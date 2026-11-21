using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Rag;
using AIEnabledRma.Rag.Knowledge;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRag(builder.Configuration);

var app = builder.Build();

// Stays open: a liveness probe that says nothing about the corpus. Everything else below is
// operational, and sits behind RagAdminGate.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// The key is read once at startup. Rotating it means restarting the host, which is the same
// operation that already applies a knowledge-base change, so it adds no new procedure.
var adminKey = app.Configuration[RagAdminGate.ApiKeyConfigurationKey];

// A keyless host outside Development is a misconfiguration that looks like a working one, so it
// fails here rather than serving the operational endpoints to a proxy on the same machine.
RagAdminGate.EnsureStartupAllowed(adminKey, app.Environment.IsDevelopment());

// The corpus is a second startup precondition, checked before anything is mapped. Resolving the
// load result here is also what makes the initial load eager: a host that only reads its corpus
// when the first request arrives can report healthy indefinitely while serving nothing, and the
// guard would never run.
var loadedAtStartup = app.Services.GetRequiredService<KnowledgeBaseLoadResult>();
KnowledgeBaseGate.EnsureStartupAllowed(loadedAtStartup, app.Environment.IsDevelopment());

var operations = app.MapGroup(string.Empty)
    .AddEndpointFilter(async (context, next) =>
        RagAdminGate.IsAllowed(context.HttpContext, adminKey)
            ? await next(context)
            : Results.NotFound());

// Reports what the retriever would actually load, and any per-file load errors. This is the
// first thing to check when retrieval looks wrong, because a silently empty corpus produces
// a confidently unhelpful assistant rather than an error. It names the directory it read and
// every file that failed to parse, which is why it is gated.
// Injects the holder rather than KnowledgeIndex itself. A KnowledgeIndex registered in DI
// would be captured once and never refreshed, so after a reload these endpoints would keep
// reporting the old corpus while the reload endpoint claimed success.
operations.MapGet("/diagnostics", (KnowledgeIndexHolder holder) =>
{
    var index = holder.Current;

    return Results.Ok(new
    {
        knowledgeBasePath = index.RootPath,
        loadedAtUtc = index.LoadedAtUtc,
        articleCount = index.Articles.Count,
        errors = index.LoadErrors,
        articles = index.Articles
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new
            {
                a.Id,
                a.Title,
                a.Category,
                productScope = a.ProductScope,
                keywordCount = a.Keywords.Count,
            }),
    });
});

// Performs a real retrieval and returns the ranked, scoped result. Deterministic, so it is
// usable as a test surface as well as an operational check.
operations.MapPost("/retrieve", (RetrieveRequest request, KnowledgeIndexHolder holder) =>
{
    var result = holder.Current.Search(new KnowledgeRetrievalQuery
    {
        Query = request.Query,
        ScopeTokens = PreTriageRules.BuildScopeTokens(request.ProductFamily, request.ProductTokens),
        CategoryHints = request.ProductFamily is null
            ? []
            : [request.ProductFamily],

        // Clamped rather than trusted. This is a diagnostic surface, but an unbounded TopK
        // lets one request ask for the entire corpus, which turns a health check into a
        // memory-amplification vector.
        TopK = Math.Clamp(request.TopK ?? 3, 1, 25),
    });

    return Results.Ok(new
    {
        result.CorpusOutOfScope,
        count = result.Articles.Count,
        articles = result.Articles,
    });
});

// Rebuilds the index from disk. Refuses to publish a corpus that produced load errors, so a
// broken article can never replace a working one. Intended for development and for an
// operator applying a reviewed knowledge-base change; it is not a file watcher.
operations.MapPost("/rag/reload", async (
    IServiceProvider services,
    KnowledgeIndexHolder holder,
    IRagReloadAudit audit,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    // Recorded whether the swap succeeds or is refused. A refusal is an attempt too, and after a
    // bad deploy the question is who asked and what the corpus looked like at the time; a
    // success-only record answers neither.
    var before = holder.Current.Articles.Count;
    var started = System.Diagnostics.Stopwatch.GetTimestamp();

    var reloaded = await services.ReloadAsync(cancellationToken);

    audit.Record(new RagReloadAttempt
    {
        AtUtc = DateTimeOffset.UtcNow,
        Caller = RagAdminGate.DescribeCaller(adminKey),
        RemoteAddress = context.Connection.RemoteIpAddress?.ToString(),
        Succeeded = reloaded,
        ArticleCountBefore = before,
        ArticleCountAfter = holder.Current.Articles.Count,
        DurationMilliseconds = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
    });

    return reloaded
        ? Results.Ok(new { status = "reloaded" })
        : Results.Problem(
            title: "Reload refused",
            detail: "The knowledge base produced load errors. The previous index is still "
                + "in service. See GET /diagnostics for the per-file errors.",
            statusCode: StatusCodes.Status422UnprocessableEntity);
});

app.Run();

internal sealed record RetrieveRequest
{
    public string Query { get; init; } = string.Empty;

    public string? ProductFamily { get; init; }

    public string[]? ProductTokens { get; init; }

    public int? TopK { get; init; }
}

/// <summary>Exposed so the test project can reference this assembly's entry point types.</summary>
public partial class Program
{
    protected Program() { }
}

/// <summary>
/// Anchor type for <c>WebApplicationFactory&lt;RagAppAnchor&gt;</c>.
/// </summary>
/// <remarks>
/// The factory only needs a public type from the assembly under test to locate its content root
/// and entry point. This project cannot be identified by its generated <c>Program</c>, because
/// the RAG, MCP and Web projects each declare one and the test project references all three.
/// </remarks>
public sealed class RagAppAnchor
{
}
