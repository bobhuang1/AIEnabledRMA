using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Rag;
using AIEnabledRma.Rag.Knowledge;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRag(builder.Configuration);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Reports what the retriever would actually load, and any per-file load errors. This is the
// first thing to check when retrieval looks wrong, because a silently empty corpus produces
// a confidently unhelpful assistant rather than an error.
// Injects the holder rather than KnowledgeIndex itself. A KnowledgeIndex registered in DI
// would be captured once and never refreshed, so after a reload these endpoints would keep
// reporting the old corpus while the reload endpoint claimed success.
app.MapGet("/diagnostics", (KnowledgeIndexHolder holder) =>
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
app.MapPost("/retrieve", (RetrieveRequest request, KnowledgeIndexHolder holder) =>
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
app.MapPost("/rag/reload", async (
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    var reloaded = await services.ReloadAsync(cancellationToken);

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
