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
        return await client.GetFromJsonAsync<List<PepNestSummary>>(path, JsonOptions) ?? [];
    }

    public async Task<PepNestDetails?> GetNestAsync(int year, string nestName)
    {
        using var response = await client.GetAsync($"nests/{year}/{Uri.EscapeDataString(nestName)}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PepNestDetails>(JsonOptions);
    }
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

public sealed class PepNestPart { public string? Name { get; init; } public int QtyNested { get; init; } public int QtyRequired { get; init; } }
public sealed class PepPlate { public string? Name { get; init; } public decimal Thickness { get; init; } public PepSize? Size { get; init; } public int Qty { get; init; } }
public sealed class PepSize { public decimal Width { get; init; } public decimal Length { get; init; } }
