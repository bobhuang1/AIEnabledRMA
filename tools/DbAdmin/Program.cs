using AIEnabledRma.Data;
using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Ai;
using AIEnabledRma.Rag;
using AIEnabledRma.Rag.Cases;
using AIEnabledRma.Rag.Knowledge;
using AIEnabledRma.Rag.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Small operational CLI for the database. Deliberately not part of any web host: schema
// application and seeding are operator actions, and putting them behind an HTTP endpoint
// would mean a running site could be a way to mutate production data.
//
// Usage:
//   DbAdmin migrate              apply pending migrations
//   DbAdmin migrations          list discovered, applied and pending migrations
//   DbAdmin seed                 apply migrations, then insert demo data if the catalog is empty
//   DbAdmin reset --force        drop every table, then migrate and seed
//   DbAdmin stats                row counts per table
//   DbAdmin kb-ingest [--rebuild] [--path <dir>]
//                                chunk, embed and publish the knowledge base to the pgvector store
//   DbAdmin kb-cases --file <cases.json> [--report <path>] [--publish --reviewed-by <name>]
//                                redact resolved CRM cases; dry run by default (report only)

// The content root defaults to the current working directory, which would make the tool
// fail with a bare "ConnectionStrings:Rma is not configured" when it is run as
// `dotnet run --project tools/DbAdmin` from the repository root. Pinning it to the binary
// location makes the command behave the same from any directory.
var options = new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
};

var builder = Host.CreateApplicationBuilder(options);
builder.Services.AddRmaDbContext(builder.Configuration);
builder.Services.AddEmbeddingModel(builder.Configuration);
builder.Services.AddSingleton(_ => Npgsql.NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Rma")
    ?? throw new InvalidOperationException("ConnectionStrings:Rma is not configured.")));
builder.Services.AddSingleton<PgVectorKnowledgeStore>();
builder.Services.AddOptions<ChunkingOptions>().Bind(builder.Configuration.GetSection(ChunkingOptions.SectionName));
builder.Services.AddOptions<KnowledgeIngestionOptions>().Bind(builder.Configuration.GetSection(KnowledgeIngestionOptions.SectionName));

using var host = builder.Build();

var isDevelopment = host.Services.GetRequiredService<IHostEnvironment>().IsDevelopment();

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<RmaDbContext>();
var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";

switch (command)
{
    case "migrate":
        await db.Database.MigrateAsync();
        Console.WriteLine("Migrations applied.");
        break;

    case "migrations":
        var knownMigrations = db.Database.GetMigrations().ToList();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

        Console.WriteLine($"Discovered ({knownMigrations.Count}):");
        foreach (var id in knownMigrations)
        {
            Console.WriteLine($"  {id}");
        }

        Console.WriteLine("Applied:");
        foreach (var id in applied)
        {
            Console.WriteLine($"  {id}");
        }

        Console.WriteLine("Pending:");
        if (pending.Count == 0)
        {
            Console.WriteLine("  (none)");
        }

        foreach (var id in pending)
        {
            Console.WriteLine($"  {id}");
        }

        break;

    case "seed":
        await SchemaBootstrapper.InitializeAsync(db, DemoDataSeeder.SeedAsync);
        await PrintStatsAsync(db);
        break;

    case "reset":
        if (!args.Contains("--force", StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "reset drops every table. Re-run with --force if that is what you want.");
            return 1;
        }

        // Dropping the schema rather than reversing migrations keeps this predictable:
        // there is exactly one migration, and a partially reversed schema is worse than
        // none. Refuses to run outside development.
        if (!isDevelopment)
        {
            Console.Error.WriteLine("Refusing to reset a non-development database.");
            return 1;
        }

        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;");
        await db.Database.MigrateAsync();
        await DemoDataSeeder.SeedAsync(db);

        // A dropped schema takes the sequence with it, and seeding writes RMA-2026-00001, so
        // the counter has to be put back in step or the first new return collides.
        await RmaNumberSequence.AlignAsync(db);
        Console.WriteLine("Database reset and reseeded.");
        await PrintStatsAsync(db);
        break;

    case "stats":
        await PrintStatsAsync(db);
        break;

    case "kb-ingest":
    {
        var pathIndex = Array.FindIndex(args, a => a.Equals("--path", StringComparison.OrdinalIgnoreCase));
        var root = RagServiceCollectionExtensions.ResolveKnowledgeBaseRoot(
            pathIndex >= 0 && pathIndex + 1 < args.Length ? args[pathIndex + 1] : "knowledge-base");

        // Review is required everywhere except Development, exactly as the RAG host requires it.
        var ingestion = host.Services.GetRequiredService<IOptions<KnowledgeIngestionOptions>>().Value;
        ingestion.RequireReviewed |= !isDevelopment;

        var loaded = new KnowledgeBaseLoader(NullLogger<KnowledgeBaseLoader>.Instance).Load(root, ingestion);
        var documents = ArticleDocuments.From(loaded, host.Services.GetRequiredService<IOptions<ChunkingOptions>>().Value);

        var store = host.Services.GetRequiredService<PgVectorKnowledgeStore>();
        await store.EnsureSchemaAsync(args.Contains("--rebuild", StringComparer.OrdinalIgnoreCase), CancellationToken.None);
        var report = await store.IngestAsync(KbKinds.Article, documents, CancellationToken.None);

        Console.WriteLine($"Knowledge base: {root}");
        Console.WriteLine($"  embedding model: {store.Embedder.Name}");
        Console.WriteLine(report.Unchanged
            ? $"  unchanged: generation {report.Generation} already holds these {report.Documents} article(s)."
            : $"  generation {report.Generation}: {report.Documents} article(s), {report.Chunks} chunk(s), "
              + $"{report.Embedded} embedded, {report.Reused} reused, {report.Failed} failed, "
              + (report.Activated ? "activated." : "NOT activated (the previous generation stays in service; re-run to retry)."));
        return report.Activated ? 0 : 2;
    }

    case "kb-cases":
    {
        string? Arg(string name)
        {
            var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // Only the file source is wired here. The Dataverse source needs the tenant, app
        // registration and field list from the owner (plan D1) before it can be configured.
        var file = Arg("--file");
        if (file is null)
        {
            Console.Error.WriteLine("kb-cases needs --file <cases.json>. The Dataverse source is waiting on decision D1 (plan.md).");
            return 1;
        }

        var publish = args.Contains("--publish", StringComparer.OrdinalIgnoreCase);
        var reviewer = Arg("--reviewed-by") ?? string.Empty;
        if (publish && string.IsNullOrWhiteSpace(reviewer))
        {
            Console.Error.WriteLine("--publish needs --reviewed-by <name>: a person must review the dry-run sample first.");
            return 1;
        }

        var store = host.Services.GetRequiredService<PgVectorKnowledgeStore>();
        await store.EnsureSchemaAsync(rebuild: false, CancellationToken.None);
        var pipeline = new CaseIngestionPipeline(store, host.Services.GetRequiredService<IOptions<ChunkingOptions>>().Value);
        var report = await pipeline.RunAsync(
            new FileCaseSource(file),
            publish,
            reviewer,
            DateOnly.FromDateTime(DateTime.UtcNow),
            CancellationToken.None);

        // The report holds counts and opaque case keys only, never values.
        var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        if (Arg("--report") is { } reportPath)
        {
            await File.WriteAllTextAsync(reportPath, json);
        }

        Console.WriteLine($"Cases: {report.Read} read, {report.Prepared} prepared, {report.NotEligible} not eligible, {report.Quarantined} quarantined.");
        Console.WriteLine(report.Published is null
            ? "Dry run: nothing was indexed. Review the report, then re-run with --publish --reviewed-by <name>."
            : report.Published.Unchanged
                ? "Published: unchanged."
                : $"Published generation {report.Published.Generation}: {report.Published.Documents} case(s), "
                  + (report.Published.Activated ? "activated." : "NOT activated (embedding failures; re-run)."));
        return report.Published is { Activated: false } ? 2 : 0;
    }

    default:
        Console.WriteLine("Usage: DbAdmin <migrate|migrations|seed|reset --force|stats|kb-ingest [--rebuild] [--path <dir>]|kb-cases --file <json> [--report <path>] [--publish --reviewed-by <name>]>");
        return 1;
}

return 0;

static async Task PrintStatsAsync(RmaDbContext db)
{
    Console.WriteLine($"  products:      {await db.Products.CountAsync()}");
    Console.WriteLine($"  devices:       {await db.Devices.CountAsync()}");
    Console.WriteLine($"  warranties:    {await db.Warranties.CountAsync()}");
    Console.WriteLine($"  customers:     {await db.Customers.CountAsync()}");
    Console.WriteLine($"  addresses:     {await db.Addresses.CountAsync()}");
    Console.WriteLine($"  rma_requests:  {await db.RmaRequests.CountAsync()}");
    Console.WriteLine($"  rma_lines:     {await db.RmaLines.CountAsync()}");
}


