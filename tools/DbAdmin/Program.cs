using AIEnabledRma.Data;
using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Rma;
using Microsoft.EntityFrameworkCore;

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

    default:
        Console.WriteLine("Usage: DbAdmin <migrate|migrations|seed|reset --force|stats>");
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


