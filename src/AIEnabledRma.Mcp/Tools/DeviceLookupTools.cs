using System.ComponentModel;
using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Catalog;
using ModelContextProtocol.Server;

namespace AIEnabledRma.Mcp.Tools;

/// <summary>
/// Device lookup tools exposed over MCP.
///
/// Every tool here is read-only and returns a deliberately narrow projection of the domain
/// entity. Serial numbers, MAC addresses, and IMEIs are customer-identifying, so what leaves
/// this process is no wider than an agent needs to do the job: enough to confirm "this is the
/// right unit" and to read its warranty state, and nothing else.
/// </summary>
[McpServerToolType]
public sealed class DeviceLookupTools(IDeviceRepository devices, IRmaRepository rmas)
{
    [McpServerTool(
        Name = "find_device",
        Title = "Find a device by any identifier")]
    [Description(
        "Find a returned device by serial number, MAC address, IMEI, or asset tag. Performs "
        + "fuzzy matching, so a mistyped or partially transcribed serial still resolves. "
        + "Returns the unit together with the warranty that governs it today.")]
    public async Task<DeviceLookupResponse> FindDeviceAsync(
        [Description("The serial number, MAC address, IMEI, or asset tag the customer provided.")]
        string identifier,
        [Description("Whether to include units that already have an open return. Defaults to false.")]
        bool includeUnitsWithOpenReturns = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        var result = await devices.LookupAsync(
            identifier,
            DateOnly.FromDateTime(DateTime.UtcNow),
            cancellationToken);

        if (result.Device is null)
        {
            return DeviceLookupResponse.NotFound(identifier);
        }

        var hasOpen = await devices.HasOpenRequestAsync(result.Device.Id, cancellationToken);

        if (hasOpen && !includeUnitsWithOpenReturns)
        {
            return new DeviceLookupResponse
            {
                Found = true,
                Suppressed = true,
                SuppressionReason =
                    "This unit already has a return in progress. Re-run with "
                    + "includeUnitsWithOpenReturns to see it anyway.",
                Device = ToDeviceSummary(result.Device),
                Warranty = result.GoverningWarranty is null
                    ? null
                    : ToWarrantySummary(result.GoverningWarranty),
                MatchedOn = result.MatchedOn,
                MatchScore = result.MatchScore,
            };
        }

        return new DeviceLookupResponse
        {
            Found = true,
            Device = ToDeviceSummary(result.Device),
            Warranty = result.GoverningWarranty is null
                ? null
                : ToWarrantySummary(result.GoverningWarranty),
            IsInWarranty = result.IsInWarranty,
            HasOpenReturn = hasOpen,
            MatchedOn = result.MatchedOn,
            MatchScore = result.MatchScore,
        };
    }

    [McpServerTool(
        Name = "search_devices",
        Title = "Search devices by product")]
    [Description(
        "Search for devices by product attributes when the customer has no identifier to "
        + "hand. Narrow results are returned best-first. Prefer find_device when any serial, "
        + "MAC, IMEI, or asset tag is available, since an identifier is exact and this is not.")]
    public async Task<DeviceSearchResponse> SearchDevicesAsync(
        [Description("Partial or fuzzy product name, e.g. 'widget'.")]
        string? productName = null,
        [Description("Partial or fuzzy model designation.")]
        string? model = null,
        [Description("Exact or partial SKU.")]
        string? sku = null,
        [Description("Maximum results to return. Capped at 25.")]
        int take = 10,
        CancellationToken cancellationToken = default)
    {
        var matches = await devices.SearchAsync(
            new DeviceSearchQuery
            {
                ProductName = Normalize(productName),
                Model = Normalize(model),
                Sku = Normalize(sku),
                Take = Math.Clamp(take, 1, 25),
            },
            cancellationToken);

        return new DeviceSearchResponse
        {
            MatchCount = matches.Count,
            Devices = matches.Select(ToDeviceSummary).ToList(),
        };
    }

    [McpServerTool(
        Name = "get_open_returns_for_device",
        Title = "Get open returns for a device")]
    [Description(
        "List the in-progress returns for a device, so an agent does not open a second one "
        + "for the same unit. Read-only.")]
    public async Task<OpenReturnsResponse> GetOpenReturnsAsync(
        [Description("The device id, as returned by find_device.")]
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var open = await rmas.GetOpenForDeviceAsync(deviceId, cancellationToken);

        return new OpenReturnsResponse
        {
            DeviceId = deviceId,
            Count = open.Count,
            Returns = open.Select(r => new OpenReturnSummary
            {
                Id = r.Id,
                RmaNumber = r.RmaNumber,
                Status = r.Status.ToString(),
                CreatedAtUtc = r.CreatedAtUtc,
                UpdatedAtUtc = r.UpdatedAtUtc,
                EligibilityReason = r.EligibilityReason,
            }).ToList(),
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DeviceSummary ToDeviceSummary(Device device) => new()
    {
        Id = device.Id,
        SerialNumber = device.SerialNumber,
        MacAddress = device.MacAddress,
        Imei = device.Imei,
        HardwareRevision = device.HardwareRevision,
        FirmwareVersion = device.FirmwareVersion,
        ShippedAtUtc = device.ShippedAtUtc,
        Product = device.Product is null
            ? null
            : new ProductSummary
            {
                Id = device.Product.Id,
                Sku = device.Product.Sku,
                Name = device.Product.Name,
                Category = device.Product.Category,
                Model = device.Product.Model,
            },
    };

    private static WarrantySummary ToWarrantySummary(Warranty warranty) => new()
    {
        Id = warranty.Id,
        PlanName = warranty.PlanName,
        Tier = warranty.Tier,
        StartDate = warranty.StartDate,
        EndDate = warranty.EndDate,
        IsActive = warranty.IsActive,
    };
}

public sealed record DeviceLookupResponse
{
    public required bool Found { get; init; }

    /// <summary>
    /// True when a match existed but details were withheld because the unit already has a
    /// return in progress. Distinct from <see cref="Found"/> so a caller can tell "no such
    /// device" from "found, but not shown to you".
    /// </summary>
    public bool Suppressed { get; init; }

    public string? SuppressionReason { get; init; }

    public DeviceSummary? Device { get; init; }

    public WarrantySummary? Warranty { get; init; }

    public bool IsInWarranty { get; init; }

    public bool HasOpenReturn { get; init; }

    public string? MatchedOn { get; init; }

    public double MatchScore { get; init; }

    public string? Query { get; init; }

    public static DeviceLookupResponse NotFound(string identifier) => new()
    {
        Found = false,
        Query = identifier,
        MatchedOn = null,
    };
}

public sealed record DeviceSearchResponse
{
    public required int MatchCount { get; init; }

    public required IReadOnlyList<DeviceSummary> Devices { get; init; }
}

public sealed record OpenReturnsResponse
{
    public required Guid DeviceId { get; init; }

    public required int Count { get; init; }

    public required IReadOnlyList<OpenReturnSummary> Returns { get; init; }
}

public sealed record OpenReturnSummary
{
    public required Guid Id { get; init; }

    public required string RmaNumber { get; init; }

    public required string Status { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public string? EligibilityReason { get; init; }
}

public sealed record DeviceSummary
{
    public required Guid Id { get; init; }

    public required string SerialNumber { get; init; }

    public string? MacAddress { get; init; }

    public string? Imei { get; init; }

    public string? HardwareRevision { get; init; }

    public string? FirmwareVersion { get; init; }

    public DateTimeOffset? ShippedAtUtc { get; init; }

    public ProductSummary? Product { get; init; }
}

public sealed record ProductSummary
{
    public required Guid Id { get; init; }

    public required string Sku { get; init; }

    public required string Name { get; init; }

    public string? Category { get; init; }

    public string? Model { get; init; }
}

public sealed record WarrantySummary
{
    public required Guid Id { get; init; }

    public required string PlanName { get; init; }

    public required string Tier { get; init; }

    public required DateOnly StartDate { get; init; }

    public required DateOnly EndDate { get; init; }

    public required bool IsActive { get; init; }
}
