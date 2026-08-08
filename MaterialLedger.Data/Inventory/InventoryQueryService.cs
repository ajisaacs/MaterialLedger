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

    /// <summary>
    /// Builds a base query for inventory items with all scalar filters applied in SQL.
    /// Returns an IQueryable to allow additional filtering before materialization.
    /// </summary>
    private IQueryable<ItemRow> BaseQuery(InventoryFilter filter)
    {
        var query = from item in db.InventoryItems
                    join spec in db.MaterialSpecs on item.MaterialSpecId equals spec.MaterialSpecId
                    join location in db.InventoryLocations on item.InventoryLocationId equals location.InventoryLocationId
                    join observer in db.AppUsers on item.LastObservedBy equals observer.AppUserId
                    where item.Status != InventoryItemStatus.Depleted && item.Status != InventoryItemStatus.Scrapped
                    select new { item, spec, location, observer };

        // Apply all scalar filters in SQL
        if (!string.IsNullOrWhiteSpace(filter.MaterialFamily))
            query = query.Where(x => EF.Functions.Like(x.spec.MaterialFamily, $"%{filter.MaterialFamily}%"));
        if (!string.IsNullOrWhiteSpace(filter.Grade))
            query = query.Where(x => x.spec.Grade != null && EF.Functions.Like(x.spec.Grade, $"%{filter.Grade}%"));
        if (!string.IsNullOrWhiteSpace(filter.Finish))
            query = query.Where(x => x.spec.Finish != null && EF.Functions.Like(x.spec.Finish, $"%{filter.Finish}%"));
        if (filter.ThicknessIn is not null)
            query = query.Where(x => x.spec.ThicknessIn == filter.ThicknessIn);
        if (filter.ItemKind is not null)
            query = query.Where(x => x.item.ItemKind == filter.ItemKind);
        if (filter.MinUsableWidthIn is not null)
            query = query.Where(x => x.item.UsableWidthIn != null && x.item.UsableWidthIn >= filter.MinUsableWidthIn);
        if (filter.MinUsableLengthIn is not null)
            query = query.Where(x => x.item.UsableLengthIn != null && x.item.UsableLengthIn >= filter.MinUsableLengthIn);
        if (!string.IsNullOrWhiteSpace(filter.LocationCode))
            query = query.Where(x => x.location.LocationCode == filter.LocationCode);

        return query.Select(x => new ItemRow(x.item, x.spec, x.location, x.observer, 0));
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
        // Compute active reservation counts as a separate query, materialized to dictionary
        var activeCounts = await db.MaterialReservations
            .Where(r => ActiveReservationStatuses.Contains(r.Status))
            .GroupBy(r => r.InventoryItemId)
            .Select(g => new { InventoryItemId = g.Key, Count = g.Count() })
            .ToListAsync();

        var activeLookup = activeCounts.ToDictionary(x => x.InventoryItemId, x => x.Count);

        // Execute the base query with all scalar filters applied in SQL
        var rows = await BaseQuery(filter).ToListAsync();

        // Merge active counts in-memory
        var rowsWithCounts = rows.Select(row => new ItemRow(row.Item, row.Spec, row.Location, row.Observer,
            activeLookup.TryGetValue(row.Item.InventoryItemId, out var count) ? count : 0)).ToList();

        // Apply AvailableOnly filter in-memory (needs computed AvailableQuantity)
        IEnumerable<InventoryItemSummary> results = rowsWithCounts.Select(ToSummary);
        if (filter.AvailableOnly)
            results = results.Where(x => x.AvailableQuantity > 0 && x.Status != InventoryItemStatus.Quarantined && !x.IsStale);
        return results.ToList();
    }

    public async Task<IReadOnlyList<InventoryItemSummary>> GetItemsAtLocationsAsync(IReadOnlyList<long> locationIds)
    {
        // Compute active reservation counts as a separate query, materialized to dictionary
        var activeCounts = await db.MaterialReservations
            .Where(r => ActiveReservationStatuses.Contains(r.Status))
            .GroupBy(r => r.InventoryItemId)
            .Select(g => new { InventoryItemId = g.Key, Count = g.Count() })
            .ToListAsync();

        var activeLookup = activeCounts.ToDictionary(x => x.InventoryItemId, x => x.Count);

        // Execute base query without filters, filter by location in SQL
        var query = from item in db.InventoryItems
                    join spec in db.MaterialSpecs on item.MaterialSpecId equals spec.MaterialSpecId
                    join location in db.InventoryLocations on item.InventoryLocationId equals location.InventoryLocationId
                    join observer in db.AppUsers on item.LastObservedBy equals observer.AppUserId
                    where item.Status != InventoryItemStatus.Depleted && item.Status != InventoryItemStatus.Scrapped
                        && locationIds.Contains(location.InventoryLocationId)
                    select new ItemRow(item, spec, location, observer, 0);

        var rows = await query.ToListAsync();

        // Merge active counts in-memory
        var rowsWithCounts = rows.Select(row => new ItemRow(row.Item, row.Spec, row.Location, row.Observer,
            activeLookup.TryGetValue(row.Item.InventoryItemId, out var count) ? count : 0)).ToList();

        return rowsWithCounts.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<LocationSummary>> GetRackSummariesAsync()
    {
        // Compute active reservation counts as a separate query, materialized to dictionary
        var activeCounts = await db.MaterialReservations
            .Where(r => ActiveReservationStatuses.Contains(r.Status))
            .GroupBy(r => r.InventoryItemId)
            .Select(g => new { InventoryItemId = g.Key, Count = g.Count() })
            .ToListAsync();

        var activeLookup = activeCounts.ToDictionary(x => x.InventoryItemId, x => x.Count);

        // Get all items with their spec/location/observer information
        var query = from item in db.InventoryItems
                    join spec in db.MaterialSpecs on item.MaterialSpecId equals spec.MaterialSpecId
                    join location in db.InventoryLocations on item.InventoryLocationId equals location.InventoryLocationId
                    join observer in db.AppUsers on item.LastObservedBy equals observer.AppUserId
                    where item.Status != InventoryItemStatus.Depleted && item.Status != InventoryItemStatus.Scrapped
                    select new ItemRow(item, spec, location, observer, 0);

        var rows = await query.ToListAsync();

        // Merge active counts in-memory
        var rowsWithCounts = rows.Select(row => new ItemRow(row.Item, row.Spec, row.Location, row.Observer,
            activeLookup.TryGetValue(row.Item.InventoryItemId, out var count) ? count : 0)).ToList();

        var racks = await db.InventoryLocations
            .Where(x => x.LocationType == InventoryLocationType.Rack && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToListAsync();

        return racks.Select(rack =>
        {
            var rackRows = rowsWithCounts.Where(r => (r.Location.ParentInventoryLocationId ?? r.Location.InventoryLocationId) == rack.InventoryLocationId).ToList();
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
