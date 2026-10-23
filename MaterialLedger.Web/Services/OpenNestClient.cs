using System.Net.Http.Json;
using System.Text.Json;

namespace MaterialLedger.Web.Services;

public sealed class OpenNestClient(HttpClient client)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OpenNestPage> QueryNestsAsync(OpenNestQuery query, CancellationToken ct)
    {
        // OpenNest.Server rejects unknown or repeated keys with 400, so only send keys it knows.
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Status)) parts.Add($"status={Uri.EscapeDataString(query.Status)}");
        if (!string.IsNullOrWhiteSpace(query.Search)) parts.Add($"search={Uri.EscapeDataString(query.Search.Trim())}");
        parts.Add("sort=dateCreated");
        parts.Add("order=desc");
        parts.Add($"offset={query.Offset}");
        parts.Add($"limit={query.Limit}");

        var page = await client.GetFromJsonAsync<NestPageWire>("api/nests/query?" + string.Join("&", parts), JsonOptions, ct)
            ?? throw new InvalidOperationException("OpenNest returned an empty nest page.");
        return new OpenNestPage
        {
            Items = page.Items.Select(ToRecord).ToList(),
            Total = page.Total,
            Offset = page.Offset,
            Limit = page.Limit,
        };
    }

    public async Task<OpenNestRecord?> GetNestAsync(Guid id, CancellationToken ct)
    {
        var record = await GetOrNullAsync<NestRecordWire>($"api/nests/{id}", ct);
        return record is null ? null : ToRecord(record);
    }

    public async Task<OpenNestSheets?> GetSheetsAsync(Guid id, CancellationToken ct)
    {
        var sheets = await GetOrNullAsync<NestSheetsWire>($"api/nests/{id}/sheets", ct);
        if (sheets is null) return null;
        return new OpenNestSheets
        {
            NestId = sheets.NestId,
            Revision = sheets.Revision,
            Sheets = sheets.Sheets.Select(s => new OpenNestSheetSize
            {
                Width = RoundSize(s.Width),
                Length = RoundSize(s.Length),
                Quantity = s.Quantity,
            }).ToList(),
            Parts = sheets.Parts.Select(p => new OpenNestPartCount { Name = p.Name, Required = p.Required, Nested = p.Nested }).ToList(),
        };
    }

    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var response = await client.GetAsync(path, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    // Sizes match inventory by exact equality against decimal(10,3) columns, thickness against
    // MaterialSpec's decimal(9,4); round here so float noise from OpenNest never breaks a match.
    private static decimal RoundSize(double value) => Math.Round((decimal)value, 3, MidpointRounding.AwayFromZero);
    private static decimal RoundThickness(double value) => Math.Round((decimal)value, 4, MidpointRounding.AwayFromZero);

    private static OpenNestRecord ToRecord(NestRecordWire r) => new()
    {
        Id = r.Id,
        Revision = r.Revision,
        Deleted = r.Deleted,
        Name = r.Name,
        Customer = r.Customer,
        DateCreated = r.DateCreated,
        DateModified = r.DateModified,
        Material = r.Material,
        MaterialGrade = r.MaterialGrade,
        Thickness = RoundThickness(r.Thickness),
        Status = r.Status,
        PlateCount = r.PlateCount,
        PartCount = r.PartCount,
        Comments = r.Comments,
        MadeBy = r.MadeBy,
        FileSize = r.FileSize,
        SavedAt = r.SavedAt,
    };

    // Wire shapes mirror OpenNest.Data (camelCase JSON, status as a camelCase string).
    private sealed class NestPageWire
    {
        public List<NestRecordWire> Items { get; init; } = [];
        public int Total { get; init; }
        public int Offset { get; init; }
        public int Limit { get; init; }
    }

    private sealed class NestRecordWire
    {
        public Guid Id { get; init; }
        public int Revision { get; init; }
        public bool Deleted { get; init; }
        public string? Name { get; init; }
        public string? Customer { get; init; }
        public DateTime DateCreated { get; init; }
        public DateTime DateModified { get; init; }
        public string? Material { get; init; }
        public string? MaterialGrade { get; init; }
        public double Thickness { get; init; }
        public string? Status { get; init; }
        public int PlateCount { get; init; }
        public int PartCount { get; init; }
        public string? Comments { get; init; }
        public string? MadeBy { get; init; }
        public long FileSize { get; init; }
        public DateTime SavedAt { get; init; }
    }

    private sealed class NestSheetsWire
    {
        public Guid NestId { get; init; }
        public int Revision { get; init; }
        public List<SheetSizeWire> Sheets { get; init; } = [];
        public List<PartCountWire> Parts { get; init; } = [];
    }

    private sealed class SheetSizeWire { public double Width { get; init; } public double Length { get; init; } public int Quantity { get; init; } }
    private sealed class PartCountWire { public string? Name { get; init; } public int Required { get; init; } public int Nested { get; init; } }
}

public sealed record OpenNestQuery(string? Status, string? Search, int Offset = 0, int Limit = 100);

public sealed class OpenNestPage
{
    public IReadOnlyList<OpenNestRecord> Items { get; init; } = [];
    public int Total { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
}

public sealed class OpenNestRecord
{
    public Guid Id { get; init; }
    public int Revision { get; init; }
    public bool Deleted { get; init; }
    public string? Name { get; init; }
    public string? Customer { get; init; }
    public DateTime DateCreated { get; init; }
    public DateTime DateModified { get; init; }
    public string? Material { get; init; }
    public string? MaterialGrade { get; init; }
    public decimal Thickness { get; init; }
    // draft | quote | toBeCut | hasBeenCut; kept as text so a status added later still renders.
    public string? Status { get; init; }
    public int PlateCount { get; init; }
    public int PartCount { get; init; }
    public string? Comments { get; init; }
    public string? MadeBy { get; init; }
    public long FileSize { get; init; }
    public DateTime SavedAt { get; init; }
}

public sealed class OpenNestSheets
{
    public Guid NestId { get; init; }
    public int Revision { get; init; }
    public IReadOnlyList<OpenNestSheetSize> Sheets { get; init; } = [];
    public IReadOnlyList<OpenNestPartCount> Parts { get; init; } = [];
}

/// <summary>A sheet size the nest needs, with <see cref="Width"/> the shorter side.</summary>
public sealed class OpenNestSheetSize
{
    public decimal Width { get; init; }
    public decimal Length { get; init; }
    public int Quantity { get; init; }
}

public sealed class OpenNestPartCount
{
    public string? Name { get; init; }
    public int Required { get; init; }
    public int Nested { get; init; }
}
