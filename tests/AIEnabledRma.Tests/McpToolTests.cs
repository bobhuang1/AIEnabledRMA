using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Rules;
using AIEnabledRma.Mcp.Tools;
using Microsoft.Extensions.Options;

namespace AIEnabledRma.Tests;

/// <summary>
/// These tools are read-only, but they speak to customers. The failures worth preventing
/// here are not crashes: they are answers that are confidently wrong — quoting one plan's
/// price for another, reporting a match score the matcher never produced, or telling a
/// customer a return already exists when it does not.
/// </summary>
public sealed class McpPolicyToolTests
{
    private static readonly DateOnly Today = new(2026, 9, 29);

    private static RmaPolicyOptions DefaultOptions() => new()
    {
        GracePeriodDays = 30,
        DefaultRepairFee = 99m,
        RepairPrices =
        [
            new ProductRepairPrice { Sku = "AX-200", RepairFee = 149m },
            new ProductRepairPrice { ProductName = "AX-400 Terminal", RepairFee = 69m },
        ],
        Tiers =
        [
            new WarrantyTierPolicy { Name = "standard", IsCovered = true, TurnaroundBusinessDays = 10 },
            new WarrantyTierPolicy { Name = "no-service", IsCovered = false, TurnaroundBusinessDays = 21 },
        ],
    };

    private static (PolicyTools Tools, FakeDeviceRepository Devices) BuildTools(RmaPolicyOptions? options = null)
    {
        options ??= DefaultOptions();
        var devices = new FakeDeviceRepository();
        var eligibility = new RmaPolicyEvaluator(Microsoft.Extensions.Options.Options.Create(options));
        return (new PolicyTools(devices, eligibility, Microsoft.Extensions.Options.Options.Create(options)), devices);
    }

    // ---------- coverage ----------

    [Fact]
    public async Task Unknown_identifier_is_not_treated_as_in_warranty()
    {
        var (tools, devices) = BuildTools();
        devices.LookupResult = new DeviceLookupResult { MatchScore = 0d };

        var response = await tools.CheckCoverageAsync("NO-SUCH-SERIAL", "2026-09-29", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.Valid);
        Assert.False(response.Identified);
        Assert.False(response.IsEligible);
        Assert.False(response.AllowsRmaCreation);
        Assert.Equal(nameof(RmaDecisionReason.DeviceNotFound), response.Reason);
    }

    [Fact]
    public async Task A_covered_unit_is_eligible()
    {
        var (tools, devices) = BuildTools();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        devices.Devices.Add(device);
        devices.LookupResult = new DeviceLookupResult { Device = device, IsInWarranty = true };

        var response = await tools.CheckCoverageAsync("AX-1", "2026-09-29", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.Identified);
        Assert.True(response.AllowsRmaCreation);
        Assert.Equal(nameof(RmaDecisionReason.InWarrantyConfirmedDefect), response.Reason);
    }

    [Fact]
    public async Task Open_return_is_read_from_the_database_rather_than_assumed()
    {
        var (tools, devices) = BuildTools();
        var clean = TestData.Device(serial: "CLEAN", end: Today.AddMonths(3));
        var busy = TestData.Device(serial: "BUSY", end: Today.AddMonths(3));
        devices.Devices.AddRange([clean, busy]);
        devices.OpenRequestByDevice[busy.Id] = true;

        devices.LookupResult = new DeviceLookupResult { Device = clean };
        var cleanResponse = await tools.CheckCoverageAsync("CLEAN", "2026-09-29", cancellationToken: TestContext.Current.CancellationToken);

        devices.LookupResult = new DeviceLookupResult { Device = busy };
        var busyResponse = await tools.CheckCoverageAsync("BUSY", "2026-09-29", cancellationToken: TestContext.Current.CancellationToken);

        // The tool used to default this to true and so told every customer that an open
        // return already existed, whatever the database said.
        Assert.True(cleanResponse.AllowsRmaCreation);
        Assert.Equal(
            nameof(RmaDecisionReason.DuplicateOpenRequest),
            busyResponse.Reason);
    }

    [Fact]
    public async Task An_explicit_open_return_flag_still_overrides_the_database()
    {
        var (tools, devices) = BuildTools();
        var device = TestData.Device(serial: "AX-1", end: Today.AddMonths(3));
        devices.Devices.Add(device);
        devices.LookupResult = new DeviceLookupResult { Device = device };

        var response = await tools.CheckCoverageAsync(
            "AX-1", "2026-09-29", hasOpenReturn: true, cancellationToken: TestContext.Current.CancellationToken);

        // A caller holding fresher information than this database must still be believed.
        Assert.Equal(nameof(RmaDecisionReason.DuplicateOpenRequest), response.Reason);
    }

    [Fact]
    public async Task A_malformed_today_is_rejected_before_any_lookup()
    {
        var (tools, devices) = BuildTools();

        var response = await tools.CheckCoverageAsync("AX-1", "not-a-date", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(response.Valid);
        Assert.NotNull(response.ValidationError);

        // The date is the input that makes the answer reproducible, so an unparseable one
        // must not be quietly replaced with "today".
        Assert.Equal(0, devices.LookupCallCount);
    }

    // ---------- fee quote ----------

    private static Device DeviceWithProduct(
        string serial,
        DateOnly? warrantyEnd = null,
        string sku = "AX-200",
        string name = "AX-200 Terminal")
    {
        var device = TestData.Device(serial: serial, end: warrantyEnd);
        device.Product = new Product
        {
            Id = Guid.NewGuid(),
            Sku = sku,
            Name = name,
        };
        return device;
    }

    [Fact]
    public async Task An_out_of_warranty_unit_quotes_its_charged_repair_price()
    {
        var (tools, devices) = BuildTools();
        var device = DeviceWithProduct("OLD-1", new DateOnly(2024, 1, 1));
        devices.Devices.Add(device);
        devices.LookupResult = new DeviceLookupResult { Device = device };

        var quote = await tools.GetFeeQuoteAsync("OLD-1", "2026-09-29", TestContext.Current.CancellationToken);

        Assert.True(quote.Valid);
        Assert.False(quote.PlanIsCovered);
        Assert.Equal(149m, quote.RepairFee);
        Assert.Equal(0m, quote.DepositAmount);
        Assert.Equal("AX-200", quote.ProductSku);
    }

    [Fact]
    public async Task A_covered_unit_quotes_the_repair_price_as_a_reference()
    {
        var (tools, devices) = BuildTools();
        var device = DeviceWithProduct("NEW-1", Today.AddMonths(3), sku: "AX-400", name: "AX-400 Terminal");
        devices.Devices.Add(device);
        devices.LookupResult = new DeviceLookupResult
        {
            Device = device,
            GoverningWarranty = device.Warranties.First(),
        };

        var quote = await tools.GetFeeQuoteAsync("NEW-1", "2026-09-29", TestContext.Current.CancellationToken);

        // Covered: a free replacement applies, and nothing is owed. The price is reported so
        // an agent can still answer "what would this cost without cover".
        Assert.True(quote.Valid);
        Assert.True(quote.PlanIsCovered);
        Assert.Equal(69m, quote.RepairFee);
        Assert.Contains("covered", quote.Note);
    }

    [Fact]
    public async Task A_product_without_a_price_row_falls_back_to_the_default_fee()
    {
        var (tools, devices) = BuildTools();
        var device = DeviceWithProduct("UNLISTED-1", new DateOnly(2024, 1, 1), sku: "NEW-SKU");
        devices.Devices.Add(device);
        devices.LookupResult = new DeviceLookupResult { Device = device };

        var quote = await tools.GetFeeQuoteAsync("UNLISTED-1", "2026-09-29", TestContext.Current.CancellationToken);

        // Evaluation never throws on an unlisted product, and the charge must still be
        // quotable, so the configured default applies.
        Assert.True(quote.Valid);
        Assert.Equal(99m, quote.RepairFee);
        Assert.Equal(99m, quote.ListPriceRepairFee);
    }

    [Fact]
    public async Task An_unknown_unit_cannot_be_quoted()
    {
        var (tools, devices) = BuildTools();
        devices.LookupResult = new DeviceLookupResult { MatchScore = 0d };

        var quote = await tools.GetFeeQuoteAsync("NO-SUCH-SERIAL", "2026-09-29", TestContext.Current.CancellationToken);

        // There is no product to price, so there is no price. The agent must not be left to
        // invent one or reuse another unit's.
        Assert.False(quote.Valid);
        Assert.NotNull(quote.ValidationError);
        Assert.Null(quote.ProductSku);
    }

    [Fact]
    public async Task A_malformed_today_is_rejected_before_any_quote()
    {
        var (tools, devices) = BuildTools();

        var quote = await tools.GetFeeQuoteAsync("AX-1", "not-a-date", TestContext.Current.CancellationToken);

        Assert.False(quote.Valid);
        Assert.NotNull(quote.ValidationError);
        Assert.Equal(0, devices.LookupCallCount);
    }

    [Fact]
    public async Task A_blank_identifier_is_refused()
    {
        var (tools, _) = BuildTools();

        Assert.False((await tools.GetFeeQuoteAsync("   ", "2026-09-29", TestContext.Current.CancellationToken)).Valid);
    }
}

/// <summary>Address resolution feeds a shipping label, so a weak match must never be
/// presented as a confident one.</summary>
public sealed class McpAddressResolutionTests
{
    private static (CustomerLookupTools Tools, FakeCustomerRepository Customers) Build()
    {
        var customers = new FakeCustomerRepository();
        return (new CustomerLookupTools(customers, new FakeRmaRepository()), customers);
    }

    private static AddressResolution Resolution(
        Address address, double score, bool isFallback = false, bool isDefault = false) => new()
    {
        Address = address,
        Score = score,
        IsFallback = isFallback,
        IsDefault = isDefault,
    };

    [Fact]
    public async Task A_confident_match_is_reported_with_the_score_the_matcher_produced()
    {
        var (tools, customers) = Build();
        customers.Resolution = Resolution(TestData.Address(line1: "2210 Harrison Street", city: "Oakland"), 0.83);

        var response = await tools.ResolveShippingAddressAsync(Guid.NewGuid(), "2210 Harrison", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.Resolved);
        Assert.False(response.IsFallback);
        Assert.Equal(0.83, response.MatchScore, 3);
        Assert.NotNull(response.Address);
        Assert.Equal("Oakland", response.Address.City);
    }

    [Fact]
    public async Task The_achieved_score_is_returned_even_when_it_differs_from_the_threshold()
    {
        var (tools, customers) = Build();
        customers.Resolution = Resolution(TestData.Address(), 0.76);

        var response = await tools.ResolveShippingAddressAsync(
            Guid.NewGuid(), "2210 Harrison St", minimumScore: 0.55, cancellationToken: TestContext.Current.CancellationToken);

        // Echoing the caller's own threshold back would report a 0.76 match as a perfect
        // 0.55, and the agent would read the address as confidently identified.
        Assert.NotEqual(0.55, response.MatchScore, 3);
        Assert.Equal(0.76, response.MatchScore, 3);
    }

    [Fact]
    public async Task A_weak_match_is_flagged_as_a_suggestion_not_a_resolution()
    {
        var (tools, customers) = Build();
        customers.Resolution = Resolution(TestData.Address(label: "Warehouse"), 0.08, isFallback: true);

        var response = await tools.ResolveShippingAddressAsync(Guid.NewGuid(), "1600 Amphitheatre Pkwy", cancellationToken: TestContext.Current.CancellationToken);

        // The default address is still returned so the wizard can show something, but the
        // caller is told to confirm it rather than assume it was what the customer meant.
        Assert.False(response.Resolved);
        Assert.True(response.IsFallback);
        Assert.Equal(0.08, response.MatchScore, 3);
        Assert.NotNull(response.Reason);
        Assert.NotNull(response.Address);
    }

    [Fact]
    public async Task A_default_address_may_still_resolve_when_the_match_is_strong()
    {
        var (tools, customers) = Build();
        customers.Resolution = Resolution(TestData.Address(), 0.9, isDefault: true);

        var response = await tools.ResolveShippingAddressAsync(Guid.NewGuid(), "1 Infinite Loop", 0.55, cancellationToken: TestContext.Current.CancellationToken);

        // "Default" describes which row was chosen, not how sure we are about it, so it must
        // not be mistaken for a confidence downgrade.
        Assert.True(response.Resolved);
        Assert.False(response.IsFallback);
    }

    [Fact]
    public async Task A_score_below_a_raised_threshold_is_not_accepted()
    {
        var (tools, customers) = Build();
        customers.Resolution = Resolution(TestData.Address(), 0.6);

        var response = await tools.ResolveShippingAddressAsync(
            Guid.NewGuid(), "1 Infinite Loop", minimumScore: 0.85, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(response.Resolved);
        Assert.NotNull(response.Reason);
    }

    [Fact]
    public async Task A_customer_with_no_saved_address_is_reported_as_such()
    {
        var (tools, customers) = Build();
        customers.Resolution = null;

        var response = await tools.ResolveShippingAddressAsync(Guid.NewGuid(), "somewhere", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(response.Resolved);
        Assert.Null(response.Address);
        Assert.NotNull(response.Reason);
    }

    [Fact]
    public async Task A_blank_typed_address_is_rejected()
    {
        var (tools, _) = Build();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => tools.ResolveShippingAddressAsync(
                Guid.NewGuid(), "   ", cancellationToken: TestContext.Current.CancellationToken));
    }
}
