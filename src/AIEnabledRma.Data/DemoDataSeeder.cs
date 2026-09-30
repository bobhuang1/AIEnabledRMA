using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Rma;

namespace AIEnabledRma.Data;

/// <summary>
/// Deterministic demo data. Every name, address, serial, and product here is fictional and
/// generated from a fixed seed, so the catalog is byte-identical on every developer machine
/// and in CI. Nothing in this file refers to a real person, company, or product.
/// </summary>
public static class DemoDataSeeder
{
    public static Task SeedAsync(RmaDbContext db, CancellationToken cancellationToken) =>
        DemoDataSeederCore.SeedAsync(db, cancellationToken);

    public static Task SeedAsync(RmaDbContext db) => SeedAsync(db, CancellationToken.None);
}

internal static class DemoDataSeederCore
{
    /// <summary>
    /// Reference date for the seed. Warranty windows are expressed relative to this so the
    /// fixture has a mix of in-warranty, grace-period, and expired devices without the data
    /// going stale on a particular day.
    /// </summary>
    private static readonly DateOnly Anchor = new(2026, 3, 1);

    public static async Task SeedAsync(RmaDbContext db, CancellationToken cancellationToken)
    {
        var now = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

        var products = BuildProducts(now);
        var devices = BuildDevices(products, now);
        var customers = BuildCustomers(now);
        var addresses = BuildAddresses(customers);

        db.Products.AddRange(products);
        db.Devices.AddRange(devices);
        db.Warranties.AddRange(BuildWarranties(devices));
        db.Customers.AddRange(customers);
        db.Addresses.AddRange(addresses);

        await db.SaveChangesAsync(cancellationToken);

        // A couple of pre-existing open requests, so the duplicate-serial rule has something
        // to find on a fresh database.
        var firstOpenDevice = devices.First(d => d.SerialNumber.EndsWith("0002", StringComparison.Ordinal));
        var firstCustomer = customers[0];

        db.RmaRequests.Add(new RmaRequest
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RmaNumber = "RMA-2026-00001",
            CustomerId = firstCustomer.Id,
            ShipToAddressId = addresses[0].Id,
            Status = RmaRequestStatus.AwaitingShipment,
            Kind = RmaRequestKind.Warranty,
            CurrencyCode = "USD",
            EligibilityReason = nameof(Domain.Rules.RmaDecisionReason.InWarrantyConfirmedDefect),
            PolicyRuleTrace = "warranty.active, warranty.tier:standard",
            TroubleshootingSummary = "Will not power on. Hard reset produced no indicator flash.",
            CreatedAtUtc = now.AddDays(-3),
            UpdatedAtUtc = now.AddDays(-3),
            Lines =
            [
                new RmaLine
                {
                    Id = Guid.Parse("21111111-1111-1111-1111-111111111111"),
                    DeviceId = firstOpenDevice.Id,
                    ProblemDescription = "Unit will not power on after a hard reset and a full charge.",
                    ProblemCategoryCode = "power",
                    WhatCustomerTried = "Hard reset, charged on a second cradle",
                    TriageVerdict = "DefectConfirmed",
                    TriageConfidence = 0.88,
                    TriageArticleIds = "kb-0003,kb-0001",
                },
            ],
        });

        await db.SaveChangesAsync(cancellationToken);

        // The seeded open request carries the reference RMA-2026-00001, which was written as a
        // literal rather than drawn from rma_number_seq. Leaving the sequence at its starting
        // value here would make the first genuine return allocate the same reference and fail
        // on ux_rma_requests_number, so the seeder leaves the database self-consistent rather
        // than leaving every caller to remember this step.
        //
        // SchemaBootstrapper and DbAdmin align again after calling this. That is not redundant
        // work and not a race: the alignment is monotonic, taking the greater of the stored
        // maximum and the sequence's own high-water mark, so it can never rewind to a reference
        // that was already issued, and callers still need it for the rows they insert
        // themselves.
        await RmaNumberSequence.AlignAsync(db, cancellationToken);
    }

    private static List<Product> BuildProducts(DateTimeOffset now) =>
    [
        new Product
        {
            Id = Guid.Parse("a0000000-0000-0000-0000-000000000001"),
            Sku = "AX-200-HS",
            Name = "AX-200 Handheld Terminal",
            Description = "General-purpose handheld data capture terminal with a charging cradle.",
            Category = "handheld-terminal",
            Model = "AX-200",
            CreatedAtUtc = now,
        },
        new Product
        {
            Id = Guid.Parse("a0000000-0000-0000-0000-000000000002"),
            Sku = "AX-400-RG",
            Name = "AX-400 Rugged Terminal",
            Description = "Ruggedised handheld terminal with a physical keypad and extended temperature range.",
            Category = "handheld-terminal",
            Model = "AX-400",
            CreatedAtUtc = now,
        },
        new Product
        {
            Id = Guid.Parse("a0000000-0000-0000-0000-000000000003"),
            Sku = "BX-100-KT",
            Name = "BX-100 Kiosk Terminal",
            Description = "Fixed-mount kiosk terminal for shared use at a service counter.",
            Category = "kiosk-terminal",
            Model = "BX-100",
            CreatedAtUtc = now,
        },
        new Product
        {
            Id = Guid.Parse("a0000000-0000-0000-0000-000000000004"),
            Sku = "CR-10-CRD",
            Name = "CR-10 Charging Cradle",
            Description = "Charging cradle for AX-series terminals. 5 V DC, 1.5 A minimum.",
            Category = "accessory",
            Model = "CR-10",
            IsMerchandise = true,
            CreatedAtUtc = now,
        },
        new Product
        {
            Id = Guid.Parse("a0000000-0000-0000-0000-000000000005"),
            Sku = "CBL-USB-3M",
            Name = "USB-C Charge Cable, 3 m",
            Description = "USB-C to cradle charge cable, 3 metre length.",
            Category = "accessory",
            Model = "CBL-3M",
            IsMerchandise = true,
            CreatedAtUtc = now,
        },
    ];

    private static List<Device> BuildProductsAndDevicesShim() => [];

    private static List<Device> BuildDevices(List<Product> products, DateTimeOffset now)
    {
        var ax200 = products[0];
        var ax400 = products[1];
        var bx100 = products[2];
        var cradle = products[3];
        var cable = products[4];

        var list = new List<Device>();

        void Add(
            Product product,
            string serial,
            string? mac,
            string? imei,
            string? hostSerial,
            int shippedDaysAgo,
            string firmware,
            string hardware)
        {
            list.Add(new Device
            {
                Id = DeterministicGuid(serial),
                ProductId = product.Id,
                SerialNumber = serial,
                MacAddress = mac,
                Imei = imei,
                HardwareRevision = hardware,
                FirmwareVersion = firmware,
                ShippedAtUtc = now.AddDays(-shippedDaysAgo),
                CreatedAtUtc = now.AddDays(-shippedDaysAgo),
                AlternateIdentifiers = hostSerial is null
                    ? []
                    :
                    [
                        new DeviceAlternateIdentifier
                        {
                            Id = DeterministicGuid(serial + "-host"),
                            Identifier = hostSerial,
                            Kind = Domain.Catalog.DeviceIdentifierKind.HostSerial,
                        },
                    ],
            });
        }

        // In warranty, healthy.
        Add(ax200, "AX2-0001-0001", "A4C1F2E30001", "356938035643809", "HOST-77120", 120, "3.2.1", "C2");
        // In warranty, and already carries an open RMA from the seed. Used to demonstrate
        // the duplicate-request rule.
        Add(ax200, "AX2-0001-0002", "A4C1F2E30002", "356938035643810", null, 90, "3.2.1", "C2");
        // In warranty, just inside the boundary.
        Add(ax200, "AX2-0001-0003", "A4C1F2E30003", "356938035643811", null, 200, "3.1.8", "C1");
        // Warranty expired, inside the grace period.
        Add(ax200, "AX2-0001-0004", "A4C1F2E30004", "356938035643812", null, 380, "3.1.8", "C1");
        // Warranty expired well beyond the grace period, but still inside the return window,
        // so the wizard offers a paid repair rather than refusing it.
        Add(ax200, "AX2-0001-0005", "A4C1F2E30005", "356938035643813", null, 500, "3.0.4", "C1");
        // Extended-tier coverage still running.
        Add(ax400, "AX4-0002-0001", "B7D2A1C00001", "356938035643820", "HOST-88431", 400, "5.0.2", "D4");
        Add(ax400, "AX4-0002-0002", "B7D2A1C00002", "356938035643821", null, 60, "5.0.2", "D4");
        // Premium tier, active, high-value unit.
        Add(ax400, "AX4-0002-0003", "B7D2A1C00003", "356938035643822", null, 700, "5.1.0", "D5");
        // Kiosk, warranty long expired.
        Add(bx100, "BX1-0003-0001", "C1E5B7D00001", "356938035643830", null, 1200, "2.4.0", "B1");
        Add(bx100, "BX1-0003-0002", "C1E5B7D00002", "356938035643831", null, 45, "2.4.6", "B1");
        // Merchandise, no electronics, no warranty record beyond a standard 30-day window.
        Add(cradle, "CR1-0004-0001", null, null, null, 25, "n/a", "A1");
        Add(cradle, "CR1-0004-0002", null, null, null, 60, "n/a", "A1");
        Add(cable, "CBL-0005-0001", null, null, null, 15, "n/a", "A1");
        Add(cable, "CBL-0005-0002", null, null, null, 200, "n/a", "A1");

        return list;
    }

    private static List<Warranty> BuildWarranties(List<Device> devices)
    {
        var warranties = new List<Warranty>();

        void Add(Device device, string plan, string tier, DateOnly start, DateOnly end, bool active = true)
        {
            warranties.Add(new Warranty
            {
                Id = DeterministicGuid(device.SerialNumber + "-w-" + plan),
                DeviceId = device.Id,
                PlanName = plan,
                Tier = tier,
                StartDate = start,
                EndDate = end,
                IsActive = active,
                CreatedAtUtc = device.CreatedAtUtc,
            });
        }

        var bySerial = devices.ToDictionary(d => d.SerialNumber, StringComparer.Ordinal);

        // Standard two-year coverage, running.
        Add(bySerial["AX2-0001-0001"], "Standard Coverage", "standard", Anchor.AddYears(-2), Anchor.AddYears(0).AddMonths(8));
        Add(bySerial["AX2-0001-0002"], "Standard Coverage", "standard", Anchor.AddYears(-2), Anchor.AddMonths(6));
        Add(bySerial["AX2-0001-0003"], "Standard Coverage", "standard", Anchor.AddYears(-2), Anchor.AddMonths(2));
        // Ended a month ago: inside the 30-day grace period.
        Add(bySerial["AX2-0001-0004"], "Standard Coverage", "standard", Anchor.AddYears(-2), Anchor.AddDays(-30));
        // Ended long ago.
        Add(bySerial["AX2-0001-0005"], "Standard Coverage", "standard", Anchor.AddYears(-4), Anchor.AddYears(-2));

        // Extended tier still running on a four-year-old unit.
        Add(bySerial["AX4-0002-0001"], "Extended Coverage Plus", "extended", Anchor.AddYears(-4), Anchor.AddMonths(10));
        Add(bySerial["AX4-0002-0002"], "Extended Coverage Plus", "extended", Anchor.AddYears(-4), Anchor.AddYears(2));
        // Premium tier.
        Add(bySerial["AX4-0002-0003"], "Premium Care", "premium", Anchor.AddYears(-4), Anchor.AddYears(1));
        // A second, superseded record on the same unit, to prove the evaluator picks the
        // record with the latest end date rather than the first row.
        Add(bySerial["AX4-0002-0003"], "Standard Coverage", "standard", Anchor.AddYears(-4), Anchor.AddDays(-200));

        Add(bySerial["BX1-0003-0001"], "Standard Coverage", "standard", Anchor.AddYears(-4), Anchor.AddYears(-2));
        Add(bySerial["BX1-0003-0002"], "Standard Coverage", "standard", Anchor.AddYears(-4), Anchor.AddDays(-90));

        // Merchandise: 30-day return only, so the accessories are deliberately out of
        // warranty at the anchor date except one.
        Add(bySerial["CR1-0004-0001"], "Accessory Return Window", "standard", Anchor.AddDays(-25), Anchor.AddDays(5));
        Add(bySerial["CR1-0004-0002"], "Accessory Return Window", "standard", Anchor.AddDays(-60), Anchor.AddDays(-30));
        Add(bySerial["CBL-0005-0001"], "Accessory Return Window", "standard", Anchor.AddDays(-15), Anchor.AddDays(15));
        Add(bySerial["CBL-0005-0002"], "Accessory Return Window", "standard", Anchor.AddDays(-200), Anchor.AddDays(-170));

        // A revoked record, to prove IsActive is honoured.
        Add(bySerial["BX1-0003-0001"], "Extended Coverage Plus", "extended", Anchor.AddYears(-1), Anchor.AddYears(3), active: false);

        return warranties;
    }

    private static List<Customer> BuildCustomers(DateTimeOffset now) =>
    [
        Customer(0, "Josephine", "Smyth", "j.smith@example.com", "+1 415 555 0142", "Warehouse North", now, "EXT-10001"),
        Customer(1, "Marcus", "Oyelaran", "m.oyelaran@example.org", "+44 20 7946 0813", "Site 2", now, "EXT-10002"),
        Customer(2, "Wren", "Kobayashi", "w.kobayashi@example.net", "+81 3 5555 0177", "Head Office", now, "EXT-10003"),
        Customer(3, "Ana", "Beltrán", "a.beltran@example.com", "+34 91 555 0199", "Sucursal Central", now, "EXT-10004"),
        Customer(4, "Devendra", "Raghunathan", "d.raghunathan@example.org", "+91 22 5555 0166", "Plant", now, "EXT-10005"),
        // Deliberately awkward name, to exercise the fuzzy matcher.
        Customer(5, "Anne-Marie", "O'Brien-Smyth", "am.obrien@example.com", "+61 2 5550 0188", "Sydney Depot", now, "EXT-10006"),
        Customer(6, "Bo", "Zhang", "b.zhang@example.net", "+86 21 5555 0155", "Shanghai Branch", now, "EXT-10007"),
        Customer(7, "Fatima", "Al-Rashid", "f.alrashid@example.com", "+971 4 555 0121", "Regional Office", now, "EXT-10008"),
    ];

    private static List<Address> BuildAddresses(List<Customer> customers)
    {
        // Each address is declared as a complete postal record. Pairing a customer to a
        // country by array index instead would silently produce a Sydney address with a US
        // country code, which is the kind of fixture bug that only surfaces when someone
        // reads a customer's details back to them.
        var specs = new (int CustomerIndex, string Label, string Line1, string? Line2,
            string City, string State, string Postal, string Country, bool IsDefault)[]
        {
            (0, "Warehouse North", "1180 Folsom Street", "Dock 3",
                "San Francisco", "CA", "94103", "US", true),
            (0, "Secondary", "2210 Harrison Street", null,
                "Oakland", "CA", "94607", "US", false),
            (1, "Site 2", "40 Rivington Street", "Unit 7",
                "London", "ENG", "EC2A 3QP", "GB", true),
            (1, "Secondary", "9 Barnes Street", null,
                "London", "ENG", "EC1A 1AA", "GB", false),
            (2, "Head Office", "2-1 Marunouchi", "Shinjuku Tower 14F",
                "Tokyo", "13", "100-6390", "JP", true),
            (3, "Sucursal Central", "Calle Gran Via 28", "Planta 4",
                "Madrid", "MD", "28013", "ES", true),
            (4, "Plant", "Andheri East", "Building C",
                "Mumbai", "MH", "400069", "IN", true),
            (5, "Sydney Depot", "45 Harbour Street", "Bay 6",
                "Sydney", "NSW", "2000", "AU", true),
            (6, "Shanghai Branch", "100 Jianguo Road", "Floor 22",
                "Shanghai", "31", "200021", "CN", true),
            (7, "Regional Office", "Sheikh Zayed Road", "Tower 2, 18F",
                "Dubai", "DU", "22511", "AE", true),
        };

        var list = new List<Address>();

        foreach (var s in specs)
        {
            var customer = customers[s.CustomerIndex];

            list.Add(new Address
            {
                Id = DeterministicGuid($"address-{s.CustomerIndex}-{s.Label}"),
                CustomerId = customer.Id,
                Label = s.Label,
                Line1 = s.Line1,
                Line2 = s.Line2,
                City = s.City,
                StateOrProvince = s.State,
                PostalCode = s.Postal,
                CountryCode = s.Country,
                IsDefault = s.IsDefault,
            });
        }

        return list;
    }

    /// <summary>
    /// Builds a customer. Locale fields are not parameters: a customer's address lives on
    /// <see cref="Address"/>, and the demo data declares each customer's address in
    /// <see cref="BuildAddresses"/> so the two cannot disagree.
    /// </summary>
    private static Customer Customer(
        int index,
        string first,
        string last,
        string email,
        string phone,
        string label,
        DateTimeOffset now,
        string externalId) => new()
    {
        Id = DeterministicGuid($"customer-{index}"),
        ExternalCustomerId = externalId,
        FirstName = first,
        LastName = last,
        CompanyName = label,
        Email = email,
        PhoneNumber = phone,
        PreferredLanguage = "en",
        CreatedAtUtc = now,
    };

    /// <summary>
    /// Stable GUID from a seed string, so ids are identical across machines and test runs.
    /// Uses SHA-256 rather than <see cref="string.GetHashCode"/>, which is randomised per
    /// process and would make seeded ids differ between runs.
    /// </summary>
    internal static Guid DeterministicGuid(string seed)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed));

        return new Guid(hash.AsSpan(0, 16));
    }
}
