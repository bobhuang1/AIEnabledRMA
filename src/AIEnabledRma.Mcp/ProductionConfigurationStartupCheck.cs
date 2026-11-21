using AIEnabledRma.Data;

namespace AIEnabledRma.Mcp;

/// <summary>
/// Refuses to start a Production tool server whose configuration is still a development one, before
/// either transport answers a call.
/// </summary>
/// <remarks>
/// Registered by <see cref="McpServices.AddRmaMcpServices"/>, the one place the stdio and HTTP
/// transports share, so the tool server cannot be covered on one and not the other. It is a hosted
/// service rather than a line in each entry point because both entry points build their own host and
/// the check belongs to the services they have in common.
/// </remarks>
internal sealed class ProductionConfigurationStartupCheck(
    IConfiguration configuration,
    IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Production only. The development and test hosts in this repository run on the compose
        // credentials on purpose, and refusing them would only teach people to bypass the check.
        if (environment.IsProduction())
        {
            ProductionConfiguration.EnsureSafe(configuration);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
