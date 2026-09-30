using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Triage;
using AIEnabledRma.Rag;

namespace AIEnabledRma.Web;

/// <summary>
/// Projects a persisted RMA into the narrow, PII-free shape the triage pipeline accepts.
/// </summary>
/// <remarks>
/// The projection is built from the stored aggregate rather than from whatever the browser
/// still has in a form field, so a tampered or stale page cannot feed the model a device that is
/// not in the return.
///
/// Deliberately carries no customer name, address, email, or phone. That is not a style
/// preference: the device block is interpolated into the model prompt, so any PII placed here
/// is one prompt-injection attempt away from leaving the system.
/// </remarks>
public static class TriageContext
{
    public static IReadOnlyList<TriageDeviceContext> Build(RmaRequest request, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Lines
            .Select(line => Build(line.Device, today))
            .ToList();
    }

    public static TriageDeviceContext Build(Device? device, DateOnly today)
    {
        if (device is null)
        {
            return new TriageDeviceContext
            {
                SerialNumber = "unknown",
                ProductName = "unknown",
            };
        }

        var product = device.Product;
        var governing = SelectWarranty(device, today);

        var tokens = new List<string> { device.SerialNumber };
        if (device.MacAddress is { Length: > 0 } mac)
        {
            tokens.Add(mac);
        }

        if (device.Imei is { Length: > 0 } imei)
        {
            tokens.Add(imei);
        }

        if (product?.Model is { Length: > 0 } model)
        {
            tokens.Add(model);
        }

        return new TriageDeviceContext
        {
            SerialNumber = device.SerialNumber,
            ProductName = product?.Name ?? "unknown",
            Sku = product?.Sku,
            Model = product?.Model,
            ProductCategory = product?.Category,
            FirmwareVersion = device.FirmwareVersion,
            IsInWarranty = governing is not null,
            WarrantyEndDate = governing?.EndDate,
            WarrantyPlanName = governing?.PlanName,
            ScopeTokens = PreTriageRules.BuildScopeTokens(product?.Category, tokens),
        };
    }

    /// <summary>
    /// The warranty that governs today: the latest one still in force. Returns null when the
    /// device is out of cover, which the policy layer treats very differently.
    /// </summary>
    private static Warranty? SelectWarranty(Device device, DateOnly today) =>
        device.Warranties
            .Where(w => w.IsActive && w.StartDate <= today && w.EndDate >= today)
            .OrderByDescending(w => w.EndDate)
            .FirstOrDefault();
}
