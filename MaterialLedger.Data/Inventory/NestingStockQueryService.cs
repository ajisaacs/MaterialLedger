using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Inventory;

/// <summary>
/// The stock a nesting program may put parts on. Material is matched exactly (family, grade and
/// finish, with a blank grade or finish meaning the catalog has none) and thickness within
/// <see cref="NestingStockQueryService.ThicknessToleranceIn"/>.
/// </summary>
public sealed record NestingStockQuery(string MaterialFamily, string? Grade, string? Finish, decimal ThicknessIn);

/// <summary>A catalog material that matched the query, whether or not any of it is in stock.</summary>
public sealed record NestingStockSpec(long MaterialSpecId, string DisplayName, decimal ThicknessIn, string? GaugeLabel);

/// <summary>One inventory item a nest can use, with how many of it are free to cut.</summary>
public sealed record NestingStockItem(
    string ItemCode,
    long MaterialSpecId,
    InventoryItemKind Kind,
    decimal WidthIn,
    decimal LengthIn,
    int AvailableQuantity,
    ShapeClassification Shape,
    string LocationCode,
    string LocationName,
    string? ParentLocationCode,
    decimal? CostPerUnit,
    DateTime LastObservedAtUtc);

public sealed record NestingStockResult(IReadOnlyList<NestingStockSpec> Specs, IReadOnlyList<NestingStockItem> Items);

/// <summary>
/// Read-only stock lookup for nesting (CutSend). Uses the same availability rules as the
/// inventory search: not quarantined, depleted or scrapped, observed within the freshness
/// threshold, and quantity left after active reservations. Items without a recorded usable
/// size cannot be nested and are left out.
/// </summary>
public sealed class NestingStockQueryService(MaterialLedgerDbContext db, int freshnessThresholdDays)
{
    public const decimal ThicknessToleranceIn = 0.001m;

    private static readonly ReservationStatus[] ActiveReservationStatuses =
    [
        ReservationStatus.Proposed, ReservationStatus.Approved, ReservationStatus.AwaitingCloseout
    ];

    public async Task<NestingStockResult> GetAsync(NestingStockQuery query, CancellationToken cancellationToken = default)
    {
        var family = query.MaterialFamily.Trim();
        var grade = string.IsNullOrWhiteSpace(query.Grade) ? null : query.Grade.Trim();
        var finish = string.IsNullOrWhiteSpace(query.Finish) ? null : query.Finish.Trim();
        var minThickness = query.ThicknessIn - ThicknessToleranceIn;
        var maxThickness = query.ThicknessIn + ThicknessToleranceIn;

        // The database collation is case-insensitive, so == matches "steel" to "Steel".
        var specs = await db.MaterialSpecs
            .Where(s => s.IsActive
                && s.MaterialFamily == family
                && (grade == null ? s.Grade == null || s.Grade == "" : s.Grade == grade)
                && (finish == null ? s.Finish == null || s.Finish == "" : s.Finish == finish)
                && s.ThicknessIn >= minThickness && s.ThicknessIn <= maxThickness)
            .OrderBy(s => s.ThicknessIn)
            .Select(s => new NestingStockSpec(s.MaterialSpecId, s.DisplayName, s.ThicknessIn, s.GaugeLabel))
            .ToListAsync(cancellationToken);
        if (specs.Count == 0)
            return new NestingStockResult(specs, []);

        var specIds = specs.Select(s => s.MaterialSpecId).ToList();
        var staleBeforeUtc = DateTime.UtcNow.AddDays(-freshnessThresholdDays);
        var rows = await (
                from item in db.InventoryItems
                join location in db.InventoryLocations on item.InventoryLocationId equals location.InventoryLocationId
                join parent in db.InventoryLocations on location.ParentInventoryLocationId equals parent.InventoryLocationId into parents
                from parent in parents.DefaultIfEmpty()
                join lot in db.SheetLots on item.SheetLotId equals lot.SheetLotId into lots
                from lot in lots.DefaultIfEmpty()
                where specIds.Contains(item.MaterialSpecId)
                    && item.Status != InventoryItemStatus.Depleted
                    && item.Status != InventoryItemStatus.Scrapped
                    && item.Status != InventoryItemStatus.Quarantined
                    && item.LastObservedAtUtc >= staleBeforeUtc
                    && item.UsableWidthIn > 0 && item.UsableLengthIn > 0
                let held = db.MaterialReservations.Count(r =>
                    r.InventoryItemId == item.InventoryItemId && ActiveReservationStatuses.Contains(r.Status))
                where item.QuantityOnHand - held >= 1
                orderby location.LocationCode, item.PublicItemCode
                select new
                {
                    item.PublicItemCode,
                    item.MaterialSpecId,
                    item.ItemKind,
                    Width = item.UsableWidthIn!.Value,
                    Length = item.UsableLengthIn!.Value,
                    Available = item.QuantityOnHand - held,
                    item.ShapeClassification,
                    location.LocationCode,
                    LocationName = location.DisplayName,
                    ParentCode = parent == null ? null : parent.LocationCode,
                    Cost = lot == null ? null : lot.CostPerUnit,
                    item.LastObservedAtUtc,
                })
            .ToListAsync(cancellationToken);

        var items = rows.Select(r => new NestingStockItem(
                r.PublicItemCode,
                r.MaterialSpecId,
                r.ItemKind,
                Math.Min(r.Width, r.Length),
                Math.Max(r.Width, r.Length),
                (int)decimal.Floor(r.Available),
                r.ShapeClassification,
                r.LocationCode,
                r.LocationName,
                r.ParentCode,
                r.Cost,
                r.LastObservedAtUtc))
            .ToList();
        return new NestingStockResult(specs, items);
    }
}
