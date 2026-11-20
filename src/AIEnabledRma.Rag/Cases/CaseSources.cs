using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIEnabledRma.Domain.Cases;

namespace AIEnabledRma.Rag.Cases;

/// <summary>
/// Reads cases from a JSON file. Used by CI and the demo with synthetic cases only; never point it
/// at an export of real CRM data.
/// </summary>
public sealed class FileCaseSource(string path) : ICaseSource
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public string Name => "file:" + Path.GetFileName(path);

    public async IAsyncEnumerable<SupportCase> ReadModifiedSinceAsync(
        DateTimeOffset? watermark,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var file = await JsonSerializer.DeserializeAsync<CaseFile>(stream, Json, cancellationToken)
            ?? throw new InvalidOperationException($"{path} holds no cases.");

        foreach (var supportCase in file.Cases.Where(c => watermark is null || c.ModifiedOn > watermark))
        {
            yield return supportCase;
        }
    }

    private sealed record CaseFile(IReadOnlyList<SupportCase> Cases);
}

/// <summary>Supplies a bearer token for the Dataverse Web API.</summary>
public interface IDataverseTokenProvider
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Dynamics 365 / Dataverse incidents through the Web API (plan D1). Read-only, incremental on
/// <c>modifiedon</c>, and the <c>$select</c> names the troubleshooting fields only: customer,
/// account, contact, owner and order lookups are never requested, so they never enter the process.
/// </summary>
/// <remarks>
/// Blocked on D1 for a real run: the tenant URL, the app registration (client credentials) and the
/// field list must come from the owner. Notes come from the case's annotations (subject and text).
/// </remarks>
public sealed class DataverseCaseSource(HttpClient http, IDataverseTokenProvider tokens) : ICaseSource
{
    /// <summary>The only incident columns requested.</summary>
    public const string IncidentSelect =
        "incidentid,title,description,statecode,statuscode,modifiedon,_productid_value";

    public string Name => "dataverse";

    public async IAsyncEnumerable<SupportCase> ReadModifiedSinceAsync(
        DateTimeOffset? watermark,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var filter = "statecode eq 1"; // Resolved
        if (watermark is { } since)
        {
            filter += $" and modifiedon gt {since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
        }

        var next = $"incidents?$select={IncidentSelect}&$filter={Uri.EscapeDataString(filter)}"
            + "&$expand=Incident_Resolutions($select=description,actualend)"
            + ",Incident_Annotation($select=subject,notetext)"
            + "&$orderby=modifiedon asc";

        while (next is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(cancellationToken));
            request.Headers.Add("Prefer", "odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\",odata.maxpagesize=200");

            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var page = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

            foreach (var incident in page.RootElement.GetProperty("value").EnumerateArray())
            {
                yield return Map(incident);
            }

            next = page.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
    }

    internal static SupportCase Map(JsonElement incident)
    {
        string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var resolutions = incident.TryGetProperty("Incident_Resolutions", out var r) ? r.EnumerateArray().ToList() : [];
        var notes = incident.TryGetProperty("Incident_Annotation", out var a)
            ? a.EnumerateArray()
                .Select(n => string.Join(": ", new[] { Text(n, "subject"), Text(n, "notetext") }.Where(t => !string.IsNullOrWhiteSpace(t))))
                .Where(t => t.Length > 0)
                .ToList()
            : [];

        return new SupportCase
        {
            SourceRecordId = Text(incident, "incidentid") ?? throw new InvalidOperationException("An incident without an id."),
            Title = Text(incident, "title") ?? string.Empty,
            Description = Text(incident, "description"),
            Notes = notes,
            Resolution = string.Join("\n", resolutions.Select(x => Text(x, "description")).Where(t => !string.IsNullOrWhiteSpace(t))),
            Product = Text(incident, "_productid_value@OData.Community.Display.V1.FormattedValue"),
            Status = incident.TryGetProperty("statecode", out var state) && state.ValueKind == JsonValueKind.Number && state.GetInt32() == 1
                ? "Resolved"
                : "Active",
            ClosedOn = resolutions
                .Select(x => Text(x, "actualend"))
                .Where(t => t is not null)
                .Select(t => (DateTimeOffset?)DateTimeOffset.Parse(t!, System.Globalization.CultureInfo.InvariantCulture))
                .Max(),
            ModifiedOn = DateTimeOffset.Parse(Text(incident, "modifiedon") ?? "1970-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
