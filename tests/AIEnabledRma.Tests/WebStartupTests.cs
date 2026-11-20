using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Web.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AIEnabledRma.Tests;

/// <summary>F5/F6 checks that need no database.</summary>
public sealed class WebStartupTests
{
    [Fact]
    public void Production_without_a_real_gateway_refuses_to_start()
    {
        using var factory = new EnvironmentFactory("Production");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("FakePaymentGateway", Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void Operator_dev_sign_in_is_refused_outside_development()
    {
        using var factory = new EnvironmentFactory("Production", ("OperatorAuth:DevSignIn", "true"), ("Payments:AllowFakeGateway", "true"));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("DevSignIn", Flatten(ex), StringComparison.Ordinal);
    }

    private static readonly DateTimeOffset Created = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RmaRequestStatus.Draft, 15, true)]
    [InlineData(RmaRequestStatus.Draft, 13, false)]
    [InlineData(RmaRequestStatus.PendingPayment, 30, true)]
    [InlineData(RmaRequestStatus.AwaitingShipment, 300, false)]
    [InlineData(RmaRequestStatus.Closed, 300, false)]
    public void Only_unconfirmed_request_links_expire(RmaRequestStatus status, int ageDays, bool expired)
    {
        var request = new RmaRequest { Id = Guid.NewGuid(), RmaNumber = "RMA-2026-00001" };
        request.Status = status;
        request.CreatedAtUtc = Created;

        Assert.Equal(
            expired,
            CapabilityExpiryFilter.IsExpired(request, Created.AddDays(ageDays), new WizardSecurityOptions()));
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
