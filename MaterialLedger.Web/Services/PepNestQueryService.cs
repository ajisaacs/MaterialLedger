using System.Net.Http.Json;
using System.Text.Json;

namespace MaterialLedger.Web.Services;

public sealed class PepNestQueryService(HttpClient client)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<PepNestSummary>> GetNestsAsync(int year, string? status, string? customer)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"Status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrWhiteSpace(customer)) query.Add($"Customer={Uri.EscapeDataString(customer)}");
        var path = $"nests/{year}" + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query));
        var nests = await client.GetFromJsonAsync<List<PepNestSummary>>(path, JsonOptions) ?? [];
        var materials = await client.GetFromJsonAsync<List<PepMaterialData>>("materials", JsonOptions) ?? [];
        var materialNames = materials
            .GroupBy(x => $"{x.Number}\u001f{x.Grade}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Name, StringComparer.OrdinalIgnoreCase);

        foreach (var nest in nests)
        {
            materialNames.TryGetValue($"{nest.MaterialNumber}\u001f{nest.MaterialGrade}", out var description);
            nest.MaterialDescription = description;
        }

        return nests;
    }

    public async Task<PepNestFilterOptions> GetFilterOptionsAsync(int year)
    {
        var nests = await client.GetFromJsonAsync<List<PepNestSummary>>($"nests/{year}", JsonOptions) ?? [];
        return new PepNestFilterOptions
        {
            Statuses = DistinctSorted(nests.Select(x => x.Status)),
            Customers = DistinctSorted(nests.Select(x => x.Customer)),
        };
    }

    private static IReadOnlyList<string> DistinctSorted(IEnumerable<string?> values) => values
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(v => v!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public async Task<PepNestDetails?> GetNestAsync(int year, string nestName)
    {
        using var response = await client.GetAsync($"nests/{year}/{Uri.EscapeDataString(nestName)}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PepNestDetails>(JsonOptions);
    }
}

public sealed class PepNestFilterOptions
{
    public IReadOnlyList<string> Statuses { get; init; } = [];
    public IReadOnlyList<string> Customers { get; init; } = [];
}

public sealed class PepNestSummary
{
    public string? Name { get; init; }
    public DateTime DateCreated { get; init; }
    public string? Status { get; init; }
    public string? Customer { get; init; }
    public string? Comments { get; init; }
    public string? ProgrammedBy { get; init; }
    public int MaterialNumber { get; init; }
    public string? MaterialGrade { get; init; }
    public string? Application { get; init; }
    public string? MaterialDescription { get; set; }
}

public sealed class PepNestDetails
{
    public string? Name { get; init; }
    public DateTime DateCreated { get; init; }
    public string? Status { get; init; }
    public string? Customer { get; init; }
    public string? Comments { get; init; }
    public string? ProgrammedBy { get; init; }
    public PepMaterial? Material { get; init; }
    public IReadOnlyList<PepNestPart> Parts { get; init; } = [];
    public IReadOnlyList<PepPlate> Plates { get; init; } = [];
}

public sealed class PepMaterial
{
    public int Number { get; init; }
    public string? Grade { get; init; }
    public decimal Thickness { get; init; }
    public string? Description { get; init; }
}

public sealed class PepMaterialData
{
    public int Number { get; init; }
    public string? Name { get; init; }
    public string? Grade { get; init; }
    public decimal Thickness { get; init; }
}

public sealed class PepNestPart
{
    public string? Name { get; init; }
    // PEP currently omits size; accept either likely future wire name without a client change.
    public string? PartSize { get; init; }
    public string? Size { get; init; }
    public int QtyNested { get; init; }
    public int QtyRequired { get; init; }
}
public sealed class PepPlate { public string? Name { get; init; } public decimal Thickness { get; init; } public PepSize? Size { get; init; } public int Qty { get; init; } }
public sealed class PepSize { public decimal Width { get; init; } public decimal Length { get; init; } }
