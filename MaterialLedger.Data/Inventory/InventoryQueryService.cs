using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Inventory;

public static class MaterialFreshness
{
    public static bool IsStale(DateTime lastObservedAtUtc, int freshnessThresholdDays) =>
        (DateTime.UtcNow - lastObservedAtUtc).TotalDays > freshnessThresholdDays;
}

public record InventoryFilter(
    string? MaterialFamily,
    string? Grade,
    string? Finish,
    decimal? ThicknessIn,
    InventoryItemKind? ItemKind,
    decimal? MinUsableWidthIn,
    decimal? MinUsableLengthIn,
    string? LocationCode,
    bool AvailableOnly);

public class InventoryItemSummary
{
    public required long InventoryItemId { get; init; }
    public required string PublicItemCode { get; init; }
    public required string MaterialSpecDisplayName { get; init; }
    public required InventoryItemKind ItemKind { get; init; }
    public required InventoryItemStatus Status { get; init; }
    public required decimal QuantityOnHand { get; init; }
    public required decimal AvailableQuantity { get; init; }
    public decimal? UsableWidthIn { get; init; }
    public decimal? UsableLengthIn { get; init; }
    public required string LocationCode { get; init; }
    public required DateTime LastObservedAtUtc { get; init; }
    public required string LastObservedByDisplayName { get; init; }
    public required bool IsStale { get; init; }
}

public class LocationSummary
{
    public required string LocationCode { get; init; }
    public required string DisplayName { get; init; }
    public required int AvailableItemCount { get; init; }
    public required int ReservedItemCount { get; init; }
    public required int ExceptionItemCount { get; init; }
}

public class InventoryQueryService(MaterialLedgerDbContext db, int freshnessThresholdDays)
{
    private static readonly ReservationStatus[] ActiveReservationStatuses =
    [
        ReservationStatus.Proposed, ReservationStatus.Approved, ReservationStatus.AwaitingCloseout
    ];

    private record ItemRow(InventoryItem Item, MaterialSpec Spec, InventoryLocation Location, AppUser Observer, int ActiveCount);

    private async Task<IReadOnlyList<ItemRow>> GetBaseRowsAsync()
    {
        var activeCounts = await db.MaterialReservations
            .Where(r => ActiveReservationStatuses.Contains(r.Status))
            .GroupBy(r => r.InventoryItemId)
            .Select(g => new { InventoryItemId = g.Key, Count = g.Count() })
            .ToListAsync();

        var activeLookup = activeCounts.ToDictionary(x => x.InventoryItemId, x => x.Count);

        var query = from item in db.InventoryItems
                    join spec in db.MaterialSpecs on item.MaterialSpecId equals spec.MaterialSpecId
                    join location in db.InventoryLocations on item.InventoryLocationId equals location.InventoryLocationId
                    join observer in db.AppUsers on item.LastObservedBy equals observer.AppUserId
                    where item.Status != InventoryItemStatus.Depleted && item.Status != InventoryItemStatus.Scrapped
                    select new ItemRow(item, spec, location, observer, 0);

        var rows = await query.ToListAsync();
        return rows.Select(row => new ItemRow(row.Item, row.Spec, row.Location, row.Observer,
            activeLookup.TryGetValue(row.Item.InventoryItemId, out var count) ? count : 0)).ToList();
    }

    private InventoryItemSummary ToSummary(ItemRow row) => new()
    {
        InventoryItemId = row.Item.InventoryItemId,
        PublicItemCode = row.Item.PublicItemCode,
        MaterialSpecDisplayName = row.Spec.DisplayName,
        ItemKind = row.Item.ItemKind,
        Status = row.Item.Status,
        QuantityOnHand = row.Item.QuantityOnHand,
        AvailableQuantity = row.Item.QuantityOnHand - row.ActiveCount,
        UsableWidthIn = row.Item.UsableWidthIn,
        UsableLengthIn = row.Item.UsableLengthIn,
        LocationCode = row.Location.LocationCode,
        LastObservedAtUtc = row.Item.LastObservedAtUtc,
        LastObservedByDisplayName = row.Observer.DisplayName,
        IsStale = MaterialFreshness.IsStale(row.Item.LastObservedAtUtc, freshnessThresholdDays)
    };

    public async Task<IReadOnlyList<InventoryItemSummary>> SearchAsync(InventoryFilter filter)
    {
        var rows = await GetBaseRowsAsync();

        IEnumerable<ItemRow> filtered = rows;
        if (!string.IsNullOrWhiteSpace(filter.MaterialFamily))
            filtered = filtered.Where(x => x.Spec.MaterialFamily.Contains(filter.MaterialFamily, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.Grade))
            filtered = filtered.Where(x => x.Spec.Grade != null && x.Spec.Grade.Contains(filter.Grade, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.Finish))
            filtered = filtered.Where(x => x.Spec.Finish != null && x.Spec.Finish.Contains(filter.Finish, StringComparison.OrdinalIgnoreCase));
        if (filter.ThicknessIn is not null)
            filtered = filtered.Where(x => x.Spec.ThicknessIn == filter.ThicknessIn);
        if (filter.ItemKind is not null)
            filtered = filtered.Where(x => x.Item.ItemKind == filter.ItemKind);
        if (filter.MinUsableWidthIn is not null)
            filtered = filtered.Where(x => x.Item.UsableWidthIn != null && x.Item.UsableWidthIn >= filter.MinUsableWidthIn);
        if (filter.MinUsableLengthIn is not null)
            filtered = filtered.Where(x => x.Item.UsableLengthIn != null && x.Item.UsableLengthIn >= filter.MinUsableLengthIn);
        if (!string.IsNullOrWhiteSpace(filter.LocationCode))
            filtered = filtered.Where(x => x.Location.LocationCode == filter.LocationCode);

        IEnumerable<InventoryItemSummary> results = filtered.Select(ToSummary);
        if (filter.AvailableOnly)
            results = results.Where(x => x.AvailableQuantity > 0 && x.Status != InventoryItemStatus.Quarantined && !x.IsStale);
        return results.ToList();
    }

    public async Task<IReadOnlyList<InventoryItemSummary>> GetItemsAtLocationsAsync(IReadOnlyList<long> locationIds)
    {
        var rows = await GetBaseRowsAsync();
        var filtered = rows.Where(x => locationIds.Contains(x.Location.InventoryLocationId)).ToList();
        return filtered.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<LocationSummary>> GetRackSummariesAsync()
    {
        var rows = await GetBaseRowsAsync();
        var racks = await db.InventoryLocations
            .Where(x => x.LocationType == InventoryLocationType.Rack && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToListAsync();

        return racks.Select(rack =>
        {
            var rackRows = rows.Where(r => (r.Location.ParentInventoryLocationId ?? r.Location.InventoryLocationId) == rack.InventoryLocationId).ToList();
            return new LocationSummary
            {
                LocationCode = rack.LocationCode,
                DisplayName = rack.DisplayName,
                AvailableItemCount = rackRows.Count(r => r.Item.Status == InventoryItemStatus.Available && r.Item.QuantityOnHand - r.ActiveCount > 0),
                ReservedItemCount = rackRows.Count(r => r.Item.Status == InventoryItemStatus.Reserved),
                ExceptionItemCount = rackRows.Count(r => r.Item.Status == InventoryItemStatus.Quarantined)
            };
        }).ToList();
    }
}
