using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AIEnabledRma.Data;

/// <summary>
/// Creates the schema and indexes directly from the DbContext, for local development and
/// tests where running the EF migration tooling is unnecessary friction.
/// Production deployments should use <c>dotnet ef database update</c>.
/// </summary>
public static class SchemaBootstrapper
{
    /// <summary>
    /// Applies migrations when they exist, otherwise ensures the schema matches the model.
    /// <paramref name="seed"/> runs only on a freshly created schema, so restarting a
    /// process never duplicates or overwrites reference data.
    /// </summary>
    public static async Task InitializeAsync(
        RmaDbContext context,
        Func<RmaDbContext, CancellationToken, Task>? seed = null,
        CancellationToken cancellationToken = default)
    {
        var migrations = context.Database.GetMigrations();

        if (migrations.Any())
        {
            await context.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            await context.Database.EnsureCreatedAsync(cancellationToken);
        }

        if (seed is not null)
        {
            await SeedIfEmptyAsync(context, seed, cancellationToken);
        }
    }

    private static async Task SeedIfEmptyAsync(
        RmaDbContext context,
        Func<RmaDbContext, CancellationToken, Task> seed,
        CancellationToken cancellationToken)
    {
        if (await context.Products.AnyAsync(cancellationToken))
        {
            return;
        }

        await seed(context, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        // The demo data includes a return reference of RMA-2026-00001. Without realigning, the
        // number sequence would still be sitting at its starting value and the first genuine
        // return would collide with it.
        await RmaNumberSequence.AlignAsync(context, cancellationToken);
    }
}
