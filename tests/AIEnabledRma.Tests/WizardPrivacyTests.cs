using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Web.Controllers;

namespace AIEnabledRma.Tests;

/// <summary>
/// The wizard is anonymous, so it may only attach a customer on an exact contact match and
/// must never echo another customer's contact details or addresses in full.
/// </summary>
public sealed class WizardPrivacyTests
{
    private static readonly Customer Jane = new()
    {
        Id = Guid.NewGuid(),
        FirstName = "Jane",
        LastName = "Smith",
        Email = "j.smith@example.com",
        PhoneNumber = "+1 (555) 010-4477",
    };

    [Theory]
    [InlineData("j.smith@example.com", true)]
    [InlineData("  J.Smith@Example.com ", true)]
    [InlineData("15550104477", true)]
    [InlineData("smith", false)]
    [InlineData("j.smith@example", false)]
    [InlineData("0104477", false)]
    [InlineData("", false)]
    public void Only_An_Exact_Email_Or_Full_Phone_Is_An_Exact_Match(string typed, bool expected) =>
        Assert.Equal(expected, RmaWizardController.IsExactContactMatch(typed, Jane));

    [Fact]
    public void Candidates_Are_Masked()
    {
        Assert.Equal("Jane S.", RmaWizardController.MaskName(Jane.FirstName, Jane.LastName));

        var email = RmaWizardController.MaskEmail(Jane.Email)!;
        Assert.DoesNotContain("smith", email);
        Assert.StartsWith("j", email);
        Assert.EndsWith(".com", email);

        var phone = RmaWizardController.MaskPhone(Jane.PhoneNumber)!;
        Assert.EndsWith("77", phone);
        Assert.DoesNotContain("555", phone);
    }

    [Fact]
    public void Addresses_Are_Masked()
    {
        var masked = RmaWizardController.MaskAddress(new Address
        {
            Id = Guid.NewGuid(),
            Line1 = "742 Evergreen Terrace",
            City = "Springfield",
            PostalCode = "62704",
            CountryCode = "US",
        });

        Assert.DoesNotContain("Evergreen", masked);
        Assert.DoesNotContain("Springfield", masked);
        Assert.DoesNotContain("62704", masked);
        Assert.Contains("62", masked);
        Assert.Contains("US", masked);
    }
}
