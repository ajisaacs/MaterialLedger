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
    /// Shared four-table join (items ⋈ spec ⋈ location ⋈ observer) with the exception-status
    /// exclusion, optional scalar filters, optional location-id restriction, and optional
    /// deterministic ordering (by LocationCode then PublicItemCode) all applied in SQL, projecting
    /// to <see cref="ItemRow"/> as the final step. Everything must stay within this one query
    /// composition — EF Core cannot translate further Where/OrderBy clauses that a *caller* chains
    /// on top of an IQueryable already projected into a constructed record type like ItemRow, and
    /// C# forbids tuple-literal projections inside expression trees, so an intermediate anonymous
    /// type (usable only within a single method) is the only shape that stays queryable end to end.
    /// </summary>
    private IQueryable<ItemRow> BuildQuery(InventoryFilter? filter = null, IReadOnlyList<long>? locationIds = null, bool orderDeterministically = false)
    {
        var query = from item in db.InventoryItems
                    join spec in db.MaterialSpecs on item.MaterialSpecId equals spec.MaterialSpecId
                    join location in db.InventoryLocations on item.InventoryLocationId equals location.InventoryLocationId
                    join observer in db.AppUsers on item.LastObservedBy equals observer.AppUserId
                    where item.Status != InventoryItemStatus.Depleted && item.Status != InventoryItemStatus.Scrapped
                    select new { item, spec, location, observer };

        if (filter is not null)
        {
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
        }

        if (locationIds is not null)
            query = query.Where(x => locationIds.Contains(x.location.InventoryLocationId));

        // Keep the availability predicate in the SQL query so CountAsync can use the same
        // canonical rules without materializing inventory rows or reservation aggregates.
        if (filter?.AvailableOnly == true)
        {
            var staleBeforeUtc = DateTime.UtcNow.AddDays(-freshnessThresholdDays);
            query = query.Where(x =>
                x.item.Status != InventoryItemStatus.Quarantined &&
                x.item.LastObservedAtUtc >= staleBeforeUtc &&
                x.item.QuantityOnHand > db.MaterialReservations.Count(reservation =>
                    reservation.InventoryItemId == x.item.InventoryItemId &&
                    ActiveReservationStatuses.Contains(reservation.Status)));
        }

        return orderDeterministically
            ? query.OrderBy(x => x.location.LocationCode).ThenBy(x => x.item.PublicItemCode)
                .Select(x => new ItemRow(x.item, x.spec, x.location, x.observer, 0))
            : query.Select(x => new ItemRow(x.item, x.spec, x.location, x.observer, 0));
    }

    /// <summary>
    /// Computes active (Proposed/Approved/AwaitingCloseout) reservation counts per inventory
    /// item, materialized to a dictionary for in-memory merging against item rows.
    /// </summary>
    private async Task<Dictionary<long, int>> LoadActiveCountsAsync()
    {
        var activeCounts = await db.MaterialReservations
            .Where(r => ActiveReservationStatuses.Contains(r.Status))
            .GroupBy(r => r.InventoryItemId)
            .Select(g => new { InventoryItemId = g.Key, Count = g.Count() })
            .ToListAsync();

        return activeCounts.ToDictionary(x => x.InventoryItemId, x => x.Count);
    }

    private static List<ItemRow> WithActiveCounts(List<ItemRow> rows, Dictionary<long, int> activeLookup) =>
        rows.Select(row => new ItemRow(row.Item, row.Spec, row.Location, row.Observer,
            activeLookup.TryGetValue(row.Item.InventoryItemId, out var count) ? count : 0)).ToList();

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

    /// <summary>
    /// Retrieves the canonical availability projection for one selected inventory item. The active
    /// hold aggregate and available-quantity subtraction are translated and executed by SQL, so a
    /// caller cannot accidentally present quantity-on-hand as availability.
    /// </summary>
    public Task<InventoryItemSummary?> GetItemSummaryAsync(long inventoryItemId)
    {
        var staleBeforeUtc = DateTime.UtcNow.AddDays(-freshnessThresholdDays);
        return (from item in db.InventoryItems
                join spec in db.MaterialSpecs on item.MaterialSpecId equals spec.MaterialSpecId
                join location in db.InventoryLocations on item.InventoryLocationId equals location.InventoryLocationId
                join observer in db.AppUsers on item.LastObservedBy equals observer.AppUserId
                where item.InventoryItemId == inventoryItemId
                let activeCount = db.MaterialReservations.Count(reservation =>
                    reservation.InventoryItemId == item.InventoryItemId &&
                    ActiveReservationStatuses.Contains(reservation.Status))
                select new InventoryItemSummary
                {
                    InventoryItemId = item.InventoryItemId,
                    PublicItemCode = item.PublicItemCode,
                    MaterialSpecDisplayName = spec.DisplayName,
                    ItemKind = item.ItemKind,
                    Status = item.Status,
                    QuantityOnHand = item.QuantityOnHand,
                    AvailableQuantity = item.QuantityOnHand - activeCount,
                    UsableWidthIn = item.UsableWidthIn,
                    UsableLengthIn = item.UsableLengthIn,
                    LocationCode = location.LocationCode,
                    LastObservedAtUtc = item.LastObservedAtUtc,
                    LastObservedByDisplayName = observer.DisplayName,
                    IsStale = item.LastObservedAtUtc < staleBeforeUtc
                }).FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<InventoryItemSummary>> SearchAsync(InventoryFilter filter)
    {
        // Compute active reservation counts as a separate query, materialized to dictionary.
        // BuildQuery applies AvailableOnly in SQL before rows are materialized.
        var activeLookup = await LoadActiveCountsAsync();
        var rows = await BuildQuery(filter: filter, orderDeterministically: true).ToListAsync();
        return WithActiveCounts(rows, activeLookup).Select(ToSummary).ToList();
    }

    /// <summary>
    /// Counts inventory rows matching every <see cref="InventoryFilter"/> predicate. For
    /// AvailableOnly filters, the active-hold, exception-status, stale, depleted, and scrapped
    /// rules are evaluated by <see cref="BuildQuery"/> in SQL.
    /// </summary>
    public Task<int> CountAsync(InventoryFilter filter) => BuildQuery(filter: filter).CountAsync();

    public async Task<IReadOnlyList<InventoryItemSummary>> GetItemsAtLocationsAsync(IReadOnlyList<long> locationIds)
    {
        // Compute active reservation counts as a separate query, materialized to dictionary
        var activeLookup = await LoadActiveCountsAsync();

        // Execute base query, filter by location in SQL, deterministically ordered
        var rows = await BuildQuery(locationIds: locationIds, orderDeterministically: true).ToListAsync();

        // Merge active counts in-memory
        var rowsWithCounts = WithActiveCounts(rows, activeLookup);

        return rowsWithCounts.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<LocationSummary>> GetRackSummariesAsync()
    {
        // Compute active reservation counts as a separate query, materialized to dictionary
        var activeLookup = await LoadActiveCountsAsync();

        // Get all items with their spec/location/observer information
        var rows = await BuildQuery().ToListAsync();

        // Merge active counts in-memory
        var rowsWithCounts = WithActiveCounts(rows, activeLookup);

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
