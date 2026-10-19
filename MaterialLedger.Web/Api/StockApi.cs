using System.Globalization;
using MaterialLedger.Data.Inventory;

namespace MaterialLedger.Web.Api;

/// <summary>
/// Read-only JSON API for other shop programs. Every endpoint needs an API key
/// (<see cref="ApiKeyAuthenticationHandler"/>) and only reads, so the app's SQL login needs
/// nothing beyond the SELECT grants the inventory pages already use.
/// </summary>
public static class StockApi
{
    public static void MapStockApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(ApiKeyAuthenticationHandler.PolicyName);

        // GET /api/stock?family=Steel&grade=A36&finish=&thicknessIn=0.25
        api.MapGet("/stock", async (string? family, string? grade, string? finish, string? thicknessIn,
            NestingStockQueryService stock, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(family))
                return Results.BadRequest(new { message = "family is required." });
            if (!decimal.TryParse(thicknessIn, NumberStyles.Number, CultureInfo.InvariantCulture, out var thickness) || thickness <= 0)
                return Results.BadRequest(new { message = "thicknessIn must be a positive number of inches." });

            var result = await stock.GetAsync(new NestingStockQuery(family, grade, finish, thickness), cancellationToken);
            return Results.Ok(new StockResponse(
                result.Specs.Select(s => new StockSpecDto(s.MaterialSpecId, s.DisplayName, s.ThicknessIn, s.GaugeLabel)).ToList(),
                result.Items.Select(i => new StockItemDto(
                    i.ItemCode,
                    i.MaterialSpecId,
                    i.Kind.ToString(),
                    i.WidthIn,
                    i.LengthIn,
                    i.AvailableQuantity,
                    i.Shape.ToString(),
                    i.LocationCode,
                    i.LocationName,
                    i.ParentLocationCode,
                    i.CostPerUnit,
                    i.LastObservedAtUtc)).ToList()));
        });
    }

    /// <summary><see cref="Specs"/> lists the catalog matches even when nothing is in stock, so a
    /// caller can tell "no such material" from "none on the rack".</summary>
    public sealed record StockResponse(IReadOnlyList<StockSpecDto> Specs, IReadOnlyList<StockItemDto> Items);

    public sealed record StockSpecDto(long MaterialSpecId, string DisplayName, decimal ThicknessIn, string? GaugeLabel);

    /// <summary>Width is the short side. <see cref="Quantity"/> is what is free after active reservations.</summary>
    public sealed record StockItemDto(
        string ItemCode,
        long MaterialSpecId,
        string Kind,
        decimal WidthIn,
        decimal LengthIn,
        int Quantity,
        string Shape,
        string LocationCode,
        string LocationName,
        string? ParentLocationCode,
        decimal? CostPerUnit,
        DateTime LastObservedAtUtc);
}
