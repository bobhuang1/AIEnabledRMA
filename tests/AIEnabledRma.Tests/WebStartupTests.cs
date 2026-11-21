using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AIEnabledRma.Tests;

/// <summary>P10: the operator dev sign-in can never reach a non-development host.</summary>
public sealed class WebStartupTests
{
    [Fact]
    public void Operator_dev_sign_in_is_refused_outside_development()
    {
        using var factory = new EnvironmentFactory(
            "Production",
            ("OperatorAuth:DevSignIn", "true"),
            ("Payments:Fake:AllowInProduction", "true"));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("DevSignIn", Flatten(ex), StringComparison.Ordinal);
    }

    private static string Flatten(Exception ex) =>
        ex.InnerException is null ? ex.Message : ex.Message + " | " + Flatten(ex.InnerException);

    private sealed class EnvironmentFactory(string environment, params (string Key, string Value)[] settings) : WebApplicationFactory<global::WebAppAnchor>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Rma", "Host=localhost;Database=unused;Username=x;Password=placeholder");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }
    }
}
