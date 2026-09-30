using System.Text.Json;
using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Rma;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AIEnabledRma.Data;

public sealed class RmaDbContext(DbContextOptions<RmaDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<DeviceAlternateIdentifier> DeviceAlternateIdentifiers => Set<DeviceAlternateIdentifier>();

    public DbSet<Warranty> Warranties => Set<Warranty>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Address> Addresses => Set<Address>();

    public DbSet<RmaRequest> RmaRequests => Set<RmaRequest>();

    public DbSet<RmaLine> RmaLines => Set<RmaLine>();

    public DbSet<RmaExclusion> RmaExclusions => Set<RmaExclusion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // snake_case naming is applied by UseSnakeCaseNamingConvention() on the options
        // builder (see RmaDbContextFactory and AddRmaDbContext), because it is a
        // DbContextOptionsBuilder extension rather than a ModelBuilder one. Both call sites
        // must set it, or the model and the hand-written trigram SQL in the migration will
        // disagree about column names.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RmaDbContext).Assembly);
    }
}

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Sku).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(256).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.Category).HasMaxLength(64);
        builder.Property(p => p.Model).HasMaxLength(64);

        // NOTE: the case-insensitive unique indexes (ux_products_sku_lower and
        // ux_devices_serial_upper) are created as raw SQL in the InitialSchema migration.
        // EF Core cannot translate lower()/upper() inside an index expression at design
        // time, so modelling them here is not possible. These plain indexes back the
        // case-sensitive equality path; the raw-SQL functional indexes enforce the real
        // constraint and are the ones the repositories actually query.
        builder.HasIndex(p => p.Sku).IsUnique().HasDatabaseName("ux_products_sku");

        builder.HasMany(p => p.Devices).WithOne(d => d.Product).HasForeignKey(d => d.ProductId);
    }
}

public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("devices");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.SerialNumber).HasMaxLength(64).IsRequired();
        builder.Property(d => d.MacAddress).HasMaxLength(32);
        builder.Property(d => d.Imei).HasMaxLength(32);
        builder.Property(d => d.HardwareRevision).HasMaxLength(32);
        builder.Property(d => d.FirmwareVersion).HasMaxLength(32);
        builder.Property(d => d.ReplacementForSerialNumber).HasMaxLength(64);

        // See the note on ProductConfiguration: the case-insensitive unique index for serial
        // numbers is raw SQL in the migration. This one is the case-sensitive path.
        builder.HasIndex(d => d.SerialNumber).IsUnique().HasDatabaseName("ux_devices_serial");

        builder.HasMany(d => d.AlternateIdentifiers)
            .WithOne(a => a.Device)
            .HasForeignKey(a => a.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(d => d.Warranties)
            .WithOne(w => w.Device)
            .HasForeignKey(w => w.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DeviceAlternateIdentifierConfiguration
    : IEntityTypeConfiguration<DeviceAlternateIdentifier>
{
    public void Configure(EntityTypeBuilder<DeviceAlternateIdentifier> builder)
    {
        builder.ToTable("device_alternate_identifiers");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Identifier).HasMaxLength(128).IsRequired();
        builder.Property(a => a.Kind).HasConversion<int>();
        builder.HasIndex(a => a.Identifier).HasDatabaseName("ix_device_alt_identifier");
    }
}

public sealed class WarrantyConfiguration : IEntityTypeConfiguration<Warranty>
{
    public void Configure(EntityTypeBuilder<Warranty> builder)
    {
        builder.ToTable("warranties");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.PlanName).HasMaxLength(128).IsRequired();
        builder.Property(w => w.Tier).HasMaxLength(64).IsRequired();
        builder.Property(w => w.StartDate).HasColumnType("date");
        builder.Property(w => w.EndDate).HasColumnType("date");
        builder.HasIndex(w => new { w.DeviceId, w.EndDate }).HasDatabaseName("ix_warranty_device_end");
    }
}

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.ExternalCustomerId).HasMaxLength(64);
        builder.Property(c => c.FirstName).HasMaxLength(128).IsRequired();
        builder.Property(c => c.MiddleName).HasMaxLength(128);
        builder.Property(c => c.LastName).HasMaxLength(128).IsRequired();
        builder.Property(c => c.CompanyName).HasMaxLength(256);
        builder.Property(c => c.Email).HasMaxLength(320).IsRequired();
        builder.Property(c => c.SecondaryEmail).HasMaxLength(320);
        builder.Property(c => c.PhoneNumber).HasMaxLength(64).IsRequired();
        builder.Property(c => c.PreferredLanguage).HasMaxLength(16);
        builder.Property(c => c.TaxId).HasMaxLength(64);

        builder.Ignore(c => c.FullName);

        builder.HasIndex(c => c.Email).HasDatabaseName("ix_customers_email");
        builder.HasIndex(c => c.ExternalCustomerId).HasDatabaseName("ix_customers_external_id");

        builder.HasMany(c => c.Addresses)
            .WithOne(a => a.Customer)
            .HasForeignKey(a => a.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AddressConfiguration : IEntityTypeConfiguration<Address>
{
    public void Configure(EntityTypeBuilder<Address> builder)
    {
        builder.ToTable("addresses");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Label).HasMaxLength(64);
        builder.Property(a => a.Line1).HasMaxLength(256).IsRequired();
        builder.Property(a => a.Line2).HasMaxLength(256);
        builder.Property(a => a.Line3).HasMaxLength(256);
        builder.Property(a => a.City).HasMaxLength(128).IsRequired();
        builder.Property(a => a.StateOrProvince).HasMaxLength(128);
        builder.Property(a => a.PostalCode).HasMaxLength(32).IsRequired();
        builder.Property(a => a.CountryCode).HasMaxLength(2).IsFixedLength().IsRequired();
        builder.Property(a => a.PhoneNumber).HasMaxLength(64);
        builder.HasIndex(a => a.CustomerId).HasDatabaseName("ix_addresses_customer");
    }
}

public sealed class RmaRequestConfiguration : IEntityTypeConfiguration<RmaRequest>
{
    public void Configure(EntityTypeBuilder<RmaRequest> builder)
    {
        builder.ToTable("rma_requests");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.RmaNumber).HasMaxLength(32).IsRequired();
        builder.Property(r => r.Status).HasConversion<int>();
        builder.Property(r => r.Kind).HasConversion<int>();
        builder.Property(r => r.WarrantyTier).HasMaxLength(64);
        builder.Property(r => r.CurrencyCode).HasMaxLength(3).IsFixedLength();
        builder.Property(r => r.ShippingCharge).HasPrecision(18, 2);
        builder.Property(r => r.DepositAmount).HasPrecision(18, 2);
        builder.Property(r => r.PaymentTransactionId).HasMaxLength(128);
        builder.Property(r => r.EligibilityReason).HasMaxLength(64);
        builder.Property(r => r.CancellationReason).HasMaxLength(512);
        builder.Property(r => r.PolicyRuleTrace).HasMaxLength(2000);
        builder.Property(r => r.TroubleshootingSummary).HasMaxLength(2000);

        builder.HasIndex(r => r.RmaNumber).IsUnique().HasDatabaseName("ux_rma_requests_number");
        builder.HasIndex(r => new { r.Status, r.CreatedAtUtc }).HasDatabaseName("ix_rma_status_created");

        builder.HasOne(r => r.Customer)
            .WithMany()
            .HasForeignKey(r => r.CustomerId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(r => r.ShipToAddress)
            .WithMany()
            .HasForeignKey(r => r.ShipToAddressId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(r => r.Lines)
            .WithOne(l => l.RmaRequest)
            .HasForeignKey(l => l.RmaRequestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RmaLineConfiguration : IEntityTypeConfiguration<RmaLine>
{
    public void Configure(EntityTypeBuilder<RmaLine> builder)
    {
        builder.ToTable("rma_lines");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.ProblemCategoryCode).HasMaxLength(64);
        builder.Property(l => l.ProblemDescription).HasMaxLength(4000).IsRequired();
        builder.Property(l => l.WhatCustomerTried).HasMaxLength(4000);
        builder.Property(l => l.RecommendedStepsJson).HasMaxLength(4000);
        builder.Property(l => l.TriageVerdict).HasMaxLength(64);
        builder.Property(l => l.TriageConfidence).HasPrecision(4, 3);
        builder.Property(l => l.TriageArticleIds).HasMaxLength(1000);

        builder.HasOne(l => l.Device)
            .WithMany()
            .HasForeignKey(l => l.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);

        // A device may legitimately appear on more than one historical request, so this index
        // is not unique; the duplicate check is done against *open* statuses at query time.
        builder.HasIndex(l => l.DeviceId).HasDatabaseName("ix_rma_lines_device");
    }
}

public sealed class RmaExclusionConfiguration : IEntityTypeConfiguration<RmaExclusion>
{
    public void Configure(EntityTypeBuilder<RmaExclusion> builder)
    {
        builder.ToTable("rma_exclusions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.SerialNumber).HasMaxLength(64);
        builder.Property(e => e.MatchedOn).HasMaxLength(32);
        builder.Property(e => e.Eligibility).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Explanation).HasMaxLength(2000);

        builder.HasOne(e => e.RmaRequest)
            .WithMany(r => r.Exclusions)
            .HasForeignKey(e => e.RmaRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Device)
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.RmaRequestId).HasDatabaseName("ix_rma_exclusions_request");
    }
}
