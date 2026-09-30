using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Data;

/// <summary>Commits the tracked changes as one transaction.</summary>
public sealed class EfUnitOfWork(RmaDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        db.SaveChangesAsync(cancellationToken);
}

public static class RmaDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL context, the repositories, and the option classes the
    /// repositories and the policy evaluator depend on.
    ///
    /// Both the web host and the MCP server call this, which is the point: the fuzzy-match
    /// thresholds a customer-facing wizard uses and the ones an agent's lookup tool uses are
    /// the same configured values, so the two can never disagree about whether a serial
    /// matched.
    /// </summary>
    public static IServiceCollection AddRmaData(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DeviceLookupOptions>()
            .Bind(configuration.GetSection(DeviceLookupOptions.SectionName))
            .Validate(
                o => o.MinimumMatchScore is >= 0 and <= 1,
                "DeviceLookup:MinimumMatchScore must be between 0 and 1.")
            .ValidateOnStart();

        services.AddOptions<CustomerLookupOptions>()
            .Bind(configuration.GetSection(CustomerLookupOptions.SectionName))
            .Validate(
                o => o.MinimumMatchScore is >= 0 and <= 1,
                "CustomerLookup:MinimumMatchScore must be between 0 and 1.")
            .ValidateOnStart();

        services.AddOptions<RmaPolicyOptions>()
            .Bind(configuration.GetSection(RmaPolicyOptions.SectionName))
            .ValidateOnStart();

        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IRmaRepository, RmaRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IEligibilityService, RmaPolicyEvaluator>();

        return services;
    }

    /// <summary>Registers just the <see cref="RmaDbContext"/>, for a host that supplies its own repositories.</summary>
    public static IServiceCollection AddRmaDbContext(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<RmaDbContext>((sp, options) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Rma")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:Rma is not configured. Set it in appsettings.json, or "
                    + "via the ConnectionStrings__Rma environment variable. Never commit a "
                    + "connection string that contains a real password.");

            options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure())
                .UseSnakeCaseNamingConvention();
        });

        return services;
    }
}
