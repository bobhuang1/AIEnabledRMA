using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Rag;
using AIEnabledRma.Rag.Knowledge;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRag(builder.Configuration);
builder.Services.AddOptions<RagSecurityOptions>()
    .Bind(builder.Configuration.GetSection(RagSecurityOptions.SectionName));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Reports what the retriever would actually load, and any per-file load errors. This is the
// first thing to check when retrieval looks wrong, because a silently empty corpus produces
// a confidently unhelpful assistant rather than an error.
//
// Anonymous callers get counts only. The filesystem path, the article inventory and the error
// text (which names files) are for operators (F2).
//
// Injects the holder rather than KnowledgeIndex itself. A KnowledgeIndex registered in DI
// would be captured once and never refreshed, so after a reload these endpoints would keep
// reporting the old corpus while the reload endpoint claimed success.
app.MapGet("/diagnostics", (HttpContext context, KnowledgeIndexHolder holder, IOptions<RagSecurityOptions> security) =>
{
    var index = holder.Current;

    if (RagApiKeys.Resolve(context, security.Value, app.Environment) != RagAccess.Admin)
    {
        return Results.Ok(new
        {
            loadedAtUtc = index.LoadedAtUtc,
            articleCount = index.Articles.Count,
            errorCount = index.LoadErrors.Count,
        });
    }

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
                reviewedBy = a.ReviewedBy,
                reviewedAt = a.ReviewedAt,
                contentHash = a.ContentHash,
            }),
    });
});

// Performs a real retrieval and returns the ranked, scoped result. Needs the reader or admin
// key outside Development.
app.MapPost("/retrieve", (
    HttpContext context,
    RetrieveRequest request,
    KnowledgeIndexHolder holder,
    IOptions<RagSecurityOptions> security) =>
{
    if (RagApiKeys.Resolve(context, security.Value, app.Environment) == RagAccess.None)
    {
        return Results.Unauthorized();
    }

    var result = holder.Current.Search(new KnowledgeRetrievalQuery
    {
        Query = request.Query,
        ScopeTokens = PreTriageRules.BuildScopeTokens(request.ProductFamily, request.ProductTokens),
        CategoryHints = request.ProductFamily is null
            ? []
            : [request.ProductFamily],

        // Clamped rather than trusted. An unbounded TopK lets one request ask for the entire
        // corpus, which turns a health check into a memory-amplification vector.
        TopK = Math.Clamp(request.TopK ?? 3, 1, 25),
    });

    return Results.Ok(new
    {
        result.CorpusOutOfScope,
        count = result.Articles.Count,
        articles = result.Articles,
    });
});

// Rebuilds the index from disk. Admin only, and every attempt is logged with its outcome.
// Refuses to publish a corpus that produced load errors (including unreviewed or injected
// articles outside Development), so a broken article can never replace a working one.
app.MapPost("/rag/reload", async (
    HttpContext context,
    IServiceProvider services,
    IOptions<RagSecurityOptions> security,
    ILogger<RagApiKeysLog> audit,
    CancellationToken cancellationToken) =>
{
    var access = RagApiKeys.Resolve(context, security.Value, app.Environment);
    if (access != RagAccess.Admin)
    {
        audit.LogWarning(
            "AUDIT kb-reload refused: caller lacks the admin key (remote {Remote}).",
            context.Connection.RemoteIpAddress);
        return access == RagAccess.None ? Results.Unauthorized() : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var reloaded = await services.ReloadAsync(cancellationToken);
    var holder = services.GetRequiredService<KnowledgeIndexHolder>();

    audit.LogInformation(
        "AUDIT kb-reload by admin key (remote {Remote}): {Outcome}, {Count} article(s) in service.",
        context.Connection.RemoteIpAddress,
        reloaded ? "published" : "refused",
        holder.Current.Articles.Count);

    return reloaded
        ? Results.Ok(new { status = "reloaded" })
        : Results.Problem(
            title: "Reload refused",
            detail: "The knowledge base produced load errors. The previous index is still "
                + "in service. See GET /diagnostics (admin) for the per-file errors.",
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

/// <summary>Category for the reload audit log lines.</summary>
public sealed class RagApiKeysLog;

/// <summary>Exposed so the test project can reference this assembly's entry point types.</summary>
public partial class Program
{
    protected Program() { }
}

/// <summary>Anchor for <c>WebApplicationFactory&lt;RagAppAnchor&gt;</c> (the name Program is ambiguous across hosts).</summary>
public sealed class RagAppAnchor;
