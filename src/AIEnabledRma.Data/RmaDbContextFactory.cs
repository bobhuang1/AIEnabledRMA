using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AIEnabledRma.Data;

/// <summary>
/// Design-time factory for the EF tooling.
///
/// The <c>dotnet ef</c> commands need to construct a <see cref="RmaDbContext"/> without
/// booting a host. The connection string is read from the environment when present and
/// otherwise falls back to a placeholder — the tooling only needs a provider and a model, it
/// never connects, so a fake password here is safe and keeps a developer's real credentials
/// out of the repository.
/// </summary>
public sealed class RmaDbContextFactory : IDesignTimeDbContextFactory<RmaDbContext>
{
    /// <summary>
    /// Environment variable the tooling reads. Not a secret; a placeholder value is fine.
    /// </summary>
    public const string ConnectionStringVariable = "RMA_DESIGN_CONNECTION";

    private const string FallbackConnectionString =
        "Host=localhost;Port=5432;Database=rmadev;Username=rmauser;Password=placeholder";

    public RmaDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable)
            ?? FallbackConnectionString;

        var options = new DbContextOptionsBuilder<RmaDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(
                typeof(RmaDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new RmaDbContext(options);
    }
}
