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
    bool AvailableOnly,
    IReadOnlyCollection<string>? MaterialFamilies = null,
    IReadOnlyCollection<string>? Grades = null,
    IReadOnlyCollection<string>? Finishes = null,
    IReadOnlyCollection<decimal>? ThicknessesIn = null,
    IReadOnlyCollection<InventoryItemKind>? ItemKinds = null,
    IReadOnlyCollection<string>? LocationCodes = null);

public sealed record InventoryFilterOptions(
    IReadOnlyList<string> MaterialFamilies,
    IReadOnlyList<string> Grades,
    IReadOnlyList<string> Finishes,
    IReadOnlyList<decimal> ThicknessesIn,
    IReadOnlyList<InventoryItemKind> ItemKinds,
    IReadOnlyList<string> LocationCodes);

public class InventoryItemSummary
{
    public required long InventoryItemId { get; init; }
    public required string PublicItemCode { get; init; }
    public required string MaterialSpecDisplayName { get; init; }
    public required string MaterialDescription { get; init; }
    public required decimal ThicknessIn { get; init; }
    public string? GaugeLabel { get; init; }
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

// View-models for the Inventory Details page activity/holds sections.
public sealed class ReservationSummary
{
    public required long ReservationId { get; init; }
    public required string Status { get; init; }
    public string? WorkReference { get; init; }
    public string? Equipment { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public string? ApprovedBy { get; init; }
    public bool IsActive => Status is "Proposed" or "Approved" or "AwaitingCloseout";
}

public sealed class TransactionSummary
{
    public required long TransactionId { get; init; }
    public required string Type { get; init; }
    public DateTime OccurredAt { get; init; }
    public string? RecordedBy { get; init; }
    public string? Notes { get; init; }
    public string? ReasonCode { get; init; }
    public string? Geometry { get; init; }
    public long? ReservationId { get; init; }
}

public sealed class ExceptionSummary
{
    public required long ExceptionId { get; init; }
    public required string ExceptionType { get; init; }
    public required string Status { get; init; }
    public DateTime OpenedAt { get; init; }
    public string? Notes { get; init; }
    public string? OpenedBy { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolutionNotes { get; init; }
}

public class InventoryQueryService(MaterialLedgerDbContext db, int freshnessThresholdDays)
{
    private static readonly ReservationStatus[] ActiveReservationStatuses =
    [
        ReservationStatus.Proposed, ReservationStatus.Approved, ReservationStatus.AwaitingCloseout
    ];

    private record ItemRow(InventoryItem Item, MaterialSpec Spec, InventoryLocation Location, AppUser Observer, int ActiveCount);

    private static string DescribeMaterial(string materialFamily, string? grade, string? finish) =>
        string.Join(" ", new[] { materialFamily, grade, finish }.Where(x => !string.IsNullOrWhiteSpace(x)));

    /// <summary>
    /// Shared four-table join (items ⋈ spec ⋈ location ⋈ observer) with the exception-status
    /// exclusion, optional filters, optional location-id restriction, and optional deterministic
    /// ordering applied in SQL before projecting to <see cref="ItemRow"/>.
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
            if (filter.MaterialFamilies is not null)
                query = query.Where(x => filter.MaterialFamilies.Contains(x.spec.MaterialFamily));
            else if (!string.IsNullOrWhiteSpace(filter.MaterialFamily))
                query = query.Where(x => EF.Functions.Like(x.spec.MaterialFamily, $"%{filter.MaterialFamily}%"));

            if (filter.Grades is not null)
                query = query.Where(x => x.spec.Grade != null && filter.Grades.Contains(x.spec.Grade));
            else if (!string.IsNullOrWhiteSpace(filter.Grade))
                query = query.Where(x => x.spec.Grade != null && EF.Functions.Like(x.spec.Grade, $"%{filter.Grade}%"));

            if (filter.Finishes is not null)
                query = query.Where(x => x.spec.Finish != null && filter.Finishes.Contains(x.spec.Finish));
            else if (!string.IsNullOrWhiteSpace(filter.Finish))
                query = query.Where(x => x.spec.Finish != null && EF.Functions.Like(x.spec.Finish, $"%{filter.Finish}%"));

            if (filter.ThicknessesIn is not null)
                query = query.Where(x => filter.ThicknessesIn.Contains(x.spec.ThicknessIn));
            else if (filter.ThicknessIn is not null)
                query = query.Where(x => x.spec.ThicknessIn == filter.ThicknessIn);

            if (filter.ItemKinds is not null)
                query = query.Where(x => filter.ItemKinds.Contains(x.item.ItemKind));
            else if (filter.ItemKind is not null)
                query = query.Where(x => x.item.ItemKind == filter.ItemKind);

            if (filter.MinUsableWidthIn is not null)
                query = query.Where(x => x.item.UsableWidthIn != null && x.item.UsableWidthIn >= filter.MinUsableWidthIn);
            if (filter.MinUsableLengthIn is not null)
                query = query.Where(x => x.item.UsableLengthIn != null && x.item.UsableLengthIn >= filter.MinUsableLengthIn);

            if (filter.LocationCodes is not null)
                query = query.Where(x => filter.LocationCodes.Contains(x.location.LocationCode));
            else if (!string.IsNullOrWhiteSpace(filter.LocationCode))
                query = query.Where(x => x.location.LocationCode == filter.LocationCode);
        }

        if (locationIds is not null)
            query = query.Where(x => locationIds.Contains(x.location.InventoryLocationId));

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

    public async Task<InventoryFilterOptions> GetFilterOptionsAsync()
    {
        var rows = await (from item in db.InventoryItems
                          join spec in db.MaterialSpecs on item.MaterialSpecId equals spec.MaterialSpecId
                          join location in db.InventoryLocations on item.InventoryLocationId equals location.InventoryLocationId
                          where item.Status != InventoryItemStatus.Depleted && item.Status != InventoryItemStatus.Scrapped
                          select new { spec.MaterialFamily, spec.Grade, spec.Finish, spec.ThicknessIn, item.ItemKind, location.LocationCode })
            .AsNoTracking()
            .ToListAsync();

        return new InventoryFilterOptions(
            rows.Select(x => x.MaterialFamily).Distinct().OrderBy(x => x).ToList(),
            rows.Where(x => !string.IsNullOrWhiteSpace(x.Grade)).Select(x => x.Grade!).Distinct().OrderBy(x => x).ToList(),
            rows.Where(x => !string.IsNullOrWhiteSpace(x.Finish)).Select(x => x.Finish!).Distinct().OrderBy(x => x).ToList(),
            rows.Select(x => x.ThicknessIn).Distinct().OrderBy(x => x).ToList(),
            rows.Select(x => x.ItemKind).Distinct().OrderBy(x => x.ToString()).ToList(),
            rows.Select(x => x.LocationCode).Distinct().OrderBy(x => x).ToList());
    }

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
        MaterialDescription = DescribeMaterial(row.Spec.MaterialFamily, row.Spec.Grade, row.Spec.Finish),
        ThicknessIn = row.Spec.ThicknessIn,
        GaugeLabel = row.Spec.GaugeLabel,
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
                    MaterialDescription = spec.MaterialFamily
                        + (string.IsNullOrEmpty(spec.Grade) ? "" : " " + spec.Grade)
                        + (string.IsNullOrEmpty(spec.Finish) ? "" : " " + spec.Finish),
                    ThicknessIn = spec.ThicknessIn,
                    GaugeLabel = spec.GaugeLabel,
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

    public async Task<InventoryItemSummary?> GetSourceSheetAsync(long inventoryItemId)
    {
        var sourceItemId = await db.MaterialTransactions
            .Where(t => t.TransactionType == MaterialTransactionType.RemnantCreated && t.InventoryItemId == inventoryItemId)
            .Select(t => t.RelatedInventoryItemId)
            .FirstOrDefaultAsync();
        return sourceItemId is null ? null : await GetItemSummaryAsync(sourceItemId.Value);
    }

    public async Task<IReadOnlyList<InventoryItemSummary>> SearchAsync(InventoryFilter filter)
    {
        var activeLookup = await LoadActiveCountsAsync();
        var rows = await BuildQuery(filter: filter, orderDeterministically: true).ToListAsync();
        return WithActiveCounts(rows, activeLookup).Select(ToSummary).ToList();
    }

    public Task<int> CountAsync(InventoryFilter filter) => BuildQuery(filter: filter).CountAsync();

    // -- Detail-page queries --

    public async Task<IReadOnlyList<ReservationSummary>> GetItemReservationsAsync(long inventoryItemId)
    {
        var reservations = await (from r in db.MaterialReservations
                                  where r.InventoryItemId == inventoryItemId
                                  orderby r.CreatedAtUtc descending
                                  select new
                                  {
                                      r.MaterialReservationId,
                                      r.Status,
                                      r.Equipment,
                                      r.CreatedAtUtc,
                                      CreatedBy = r.CreatedByUser.DisplayName,
                                      ApprovedAt = r.ApprovedAtUtc,
                                      ApprovedBy = r.ApprovedByUser.DisplayName,
                                      r.ShopOpsJobId,
                                      r.DrawingId,
                                      r.DrawingRevision,
                                      r.PepNestYear,
                                      r.PepNestName
                                  }).Take(20).ToListAsync();

        return reservations.Select(r => new ReservationSummary
        {
            ReservationId = r.MaterialReservationId,
            Status = r.Status.ToString(),
            WorkReference = string.IsNullOrWhiteSpace(r.ShopOpsJobId) && string.IsNullOrWhiteSpace(r.DrawingId) && string.IsNullOrWhiteSpace(r.PepNestName)
                ? null
                : (string.IsNullOrWhiteSpace(r.ShopOpsJobId)
                    ? (string.IsNullOrWhiteSpace(r.DrawingRevision)
                        ? $"Drawing {r.DrawingId}"
                        : $"Drawing {r.DrawingId} rev {r.DrawingRevision}")
                    : $"Job {r.ShopOpsJobId}")
                        ?? (r.PepNestYear is null
                            ? $"Nest {r.PepNestName}"
                            : $"Nest {r.PepNestYear}: {r.PepNestName}"),
            Equipment = r.Equipment?.ToString(),
            CreatedAt = r.CreatedAtUtc,
            CreatedBy = r.CreatedBy,
            ApprovedAt = r.ApprovedAt,
            ApprovedBy = r.ApprovedBy
        }).ToList();
    }

    public async Task<IReadOnlyList<TransactionSummary>> GetItemTransactionsAsync(long inventoryItemId)
    {
        var transactions = await (from t in db.MaterialTransactions
                                  where t.InventoryItemId == inventoryItemId
                                  orderby t.OccurredAtUtc descending
                                  select new
                                  {
                                      t.MaterialTransactionId,
                                      t.TransactionType,
                                      t.OccurredAtUtc,
                                      t.RecordedByUser.DisplayName,
                                      t.Notes,
                                      t.ReasonCode,
                                      t.SourceSystem,
                                      Width = t.GeometryWidthInSnapshot,
                                      Length = t.GeometryLengthInSnapshot,
                                      ReservationId = t.MaterialReservationId,
                                      t.ExternalReference
                                  }).Take(30).ToListAsync();

        return transactions.Select(t => new TransactionSummary
        {
            TransactionId = t.MaterialTransactionId,
            Type = t.TransactionType.ToString(),
            OccurredAt = t.OccurredAtUtc,
            RecordedBy = t.DisplayName,
            Notes = t.Notes,
            ReasonCode = t.ReasonCode,
            Geometry = t.Width is not null && t.Length is not null
                ? $"{t.Width.Value:F1} x {t.Length.Value:F1} in"
                : null,
            ReservationId = t.ReservationId
        }).ToList();
    }

    public async Task<IReadOnlyList<ExceptionSummary>> GetItemExceptionsAsync(long inventoryItemId)
    {
        var exceptions = await (from e in db.MaterialExceptions
                                where e.InventoryItemId == inventoryItemId
                                orderby e.OpenedAtUtc descending
                                select new
                                {
                                    e.MaterialExceptionId,
                                    e.ExceptionType,
                                    e.Status,
                                    e.OpenedAtUtc,
                                    e.Notes,
                                    OpenedBy = e.OpenedByUser.DisplayName,
                                    e.ResolvedAtUtc,
                                    e.ResolvedByUser.DisplayName,
                                    e.ResolutionNotes
                                }).ToListAsync();

        return exceptions.Select(e => new ExceptionSummary
        {
            ExceptionId = e.MaterialExceptionId,
            ExceptionType = e.ExceptionType.ToString(),
            Status = e.Status.ToString(),
            OpenedAt = e.OpenedAtUtc,
            Notes = e.Notes,
            OpenedBy = e.OpenedBy,
            ResolvedAt = e.ResolvedAtUtc,
            ResolutionNotes = e.ResolutionNotes
        }).ToList();
    }

    public async Task<IReadOnlyList<InventoryItemSummary>> GetItemsAtLocationsAsync(IReadOnlyList<long> locationIds)
    {
        var activeLookup = await LoadActiveCountsAsync();
        var rows = await BuildQuery(locationIds: locationIds, orderDeterministically: true).ToListAsync();
        return WithActiveCounts(rows, activeLookup).Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<LocationSummary>> GetRackSummariesAsync()
    {
        var activeLookup = await LoadActiveCountsAsync();
        var rows = await BuildQuery().ToListAsync();
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
