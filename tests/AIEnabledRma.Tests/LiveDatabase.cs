using AIEnabledRma.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace AIEnabledRma.Tests;

/// <summary>
/// A throwaway PostgreSQL database for the suites that need real server behaviour.
/// </summary>
/// <remarks>
/// Both live suites carried their own copy of this: read <c>RMA_TEST_CONNECTION</c>, widen the
/// command timeout, probe the server, create the database, and drop it afterwards. The copies had
/// already drifted — one caught <see cref="NpgsqlException"/> on the drop and the other caught
/// every exception — so a fix to the provisioning applied to one of them silently left the other
/// on the old behaviour. It lives here once, and a suite only names the prefix its databases
/// should carry.
///
/// A database per test is deliberate: these suites assert on allocation, migration, and
/// persistence, and sharing one would let a leftover row from one test decide another. The
/// prefix makes a leaked database identifiable and safe to delete by hand.
/// </remarks>
internal sealed class LiveDatabase(string namePrefix) : IAsyncDisposable
{
    private const string ConnectionStringVariable = "RMA_TEST_CONNECTION";

    private const string DefaultAdminConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=rmauser;Password=placeholder";

    private string _name = string.Empty;
    private string _adminConnectionString = string.Empty;

    /// <summary>The database this fixture created, for a context or a host that must reach it.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _adminConnectionString = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable(ConnectionStringVariable)
            ?? DefaultAdminConnectionString)
        {
            // Provisioning creates and drops a database per test. On a workstation whose
            // PostgreSQL data directory is a bind-mounted volume — the docker-compose setup this
            // repository documents — CREATE DATABASE spends tens of seconds on fsync alone, and
            // Npgsql's 30-second default command timeout cancels it mid-copy. That surfaces as
            // "Exception while reading from stream", which reads like a broken test rather than a
            // slow disk.
            CommandTimeout = 120,
        }.ConnectionString;

        try
        {
            await using var probe = new NpgsqlConnection(_adminConnectionString);
            await probe.OpenAsync();
        }
        catch (Exception ex)
        {
            // A developer without the docker-compose database running should not see a red build
            // for behaviour that is exercised correctly elsewhere. RMA_TEST_CONNECTION points
            // these suites at a real server.
            Assert.Skip($"PostgreSQL is not reachable: {ex.GetType().Name}: {ex.Message}");
        }

        _name = $"{namePrefix}_{Guid.NewGuid():N}";
        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _name,
        }.ConnectionString;

        await using var create = new NpgsqlConnection(_adminConnectionString);
        await create.OpenAsync();

        // CREATE DATABASE cannot run inside a transaction and cannot be parameterised. The name is
        // generated from a GUID above, so there is nothing to inject.
        await using var command = create.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{_name}\"";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A context against the throwaway database, configured the way the host configures its own.
    /// </summary>
    public RmaDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<RmaDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsAssembly(
                typeof(RmaDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new RmaDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        if (_name.Length == 0)
        {
            return;
        }

        try
        {
            // Pooled connections — including the host's — would keep the database busy and make
            // the drop fail.
            NpgsqlConnection.ClearAllPools();

            await using var drop = new NpgsqlConnection(_adminConnectionString);
            await drop.OpenAsync();

            await using var command = drop.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            // A leaked throwaway database is untidy but must never fail an otherwise green run.
            // It carries the suite's prefix and is safe to delete by hand.
        }
    }
}
