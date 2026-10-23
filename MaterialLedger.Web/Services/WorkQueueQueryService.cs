using MaterialLedger.Data;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Shared;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Services;

public sealed record WorkQueueItem(
    long MaterialReservationId,
    long InventoryItemId,
    string PublicItemCode,
    string MaterialDisplayName,
    string LocationCode,
    string WorkReference,
    string Equipment,
    string WorkflowLabel,
    DateTime TimestampUtc);

public sealed class WorkQueueSummary
{
    public required int AvailableMaterialCount { get; init; }
    public required int ApprovalNeededCount { get; init; }
    public required int ReadyForCuttingCount { get; init; }
    public required int CutResultNeededCount { get; init; }
    public required int AttentionNeededCount { get; init; }
    public required IReadOnlyList<WorkQueueItem> RecentWork { get; init; }
}

/// <summary>
/// Read-only control-board projection. It does not change reservation, closeout, or inventory state.
/// </summary>
public sealed class WorkQueueQueryService(MaterialLedgerDbContext db, InventoryQueryService inventory)
{
    private const int RecentWorkLimit = 10;

    public async Task<WorkQueueSummary> GetAsync()
    {
        var availableMaterialCount = await inventory.CountAsync(new InventoryFilter(
            MaterialFamily: null, Grade: null, Finish: null, ThicknessIn: null, ItemKind: null,
            MinUsableWidthIn: null, MinUsableLengthIn: null, LocationCode: null, AvailableOnly: true));

        // Keep the queue predicates on source entities. EF Core cannot translate
        // predicates over members of a constructed ReservationProjection record.
        // The inner join intentionally excludes corrupt legacy reservations whose inventory row is absent.
        var reservationRows =
            from reservation in db.MaterialReservations.AsNoTracking()
            join item in db.InventoryItems.AsNoTracking() on reservation.InventoryItemId equals item.InventoryItemId
            select new { Reservation = reservation, Item = item };

        var approvalNeededCount = await reservationRows
            .Where(row => row.Reservation.Status == ReservationStatus.Proposed)
            .CountAsync();
        var readyForCuttingCount = await reservationRows
            .Where(row => row.Reservation.Status == ReservationStatus.Approved)
            .CountAsync();
        var cutResultNeededCount = await reservationRows
            .Where(row => row.Reservation.Status == ReservationStatus.AwaitingCloseout)
            .CountAsync();
        var recentReservationRows = reservationRows
            .OrderByDescending(row => row.Reservation.ApprovedAtUtc ?? row.Reservation.CreatedAtUtc)
            .ThenByDescending(row => row.Reservation.MaterialReservationId)
            .Take(RecentWorkLimit);
        var recentWork = await (
            from row in recentReservationRows
            join spec in db.MaterialSpecs.AsNoTracking() on row.Item.MaterialSpecId equals spec.MaterialSpecId into specs
            from spec in specs.DefaultIfEmpty()
            join location in db.InventoryLocations.AsNoTracking() on row.Item.InventoryLocationId equals location.InventoryLocationId into locations
            from location in locations.DefaultIfEmpty()
            select new ReservationProjection(
                row.Reservation.MaterialReservationId,
                row.Item.InventoryItemId,
                row.Item.PublicItemCode,
                spec == null ? null : spec.DisplayName,
                location == null ? null : location.LocationCode,
                row.Reservation.ShopOpsJobId,
                row.Reservation.DrawingId,
                row.Reservation.DrawingRevision,
                row.Reservation.PepNestYear,
                row.Reservation.PepNestName,
                row.Reservation.NestName,
                row.Reservation.Equipment,
                row.Reservation.Status,
                row.Reservation.CreatedAtUtc,
                row.Reservation.ApprovedAtUtc ?? row.Reservation.CreatedAtUtc))
            .ToListAsync();
        var attentionNeededCount = await db.MaterialExceptions.AsNoTracking()
            .Where(exceptionItem => exceptionItem.Status == MaterialExceptionStatus.Open)
            .CountAsync();

        return new WorkQueueSummary
        {
            AvailableMaterialCount = availableMaterialCount,
            ApprovalNeededCount = approvalNeededCount,
            ReadyForCuttingCount = readyForCuttingCount,
            CutResultNeededCount = cutResultNeededCount,
            AttentionNeededCount = attentionNeededCount,
            RecentWork = recentWork.Select(ToWorkQueueItem).ToList()
        };
    }


    private static WorkQueueItem ToWorkQueueItem(ReservationProjection reservation)
    {
        var workflow = ReservationWorkflowDisplay.For(reservation.Status);
        return new WorkQueueItem(
            reservation.MaterialReservationId,
            reservation.InventoryItemId,
            reservation.PublicItemCode,
            reservation.MaterialDisplayName ?? "Material specification unavailable",
            reservation.LocationCode ?? "Location unavailable",
            WorkReference(reservation.ShopOpsJobId, reservation.DrawingId, reservation.DrawingRevision, reservation.PepNestYear, reservation.PepNestName, reservation.NestName),
            reservation.Equipment?.ToString() ?? "Equipment not assigned",
            workflow.Label,
            reservation.TimestampUtc);
    }

    private static string WorkReference(string? shopOpsJobId, string? drawingId, string? drawingRevision, int? pepNestYear, string? pepNestName, string? nestName)
    {
        if (!string.IsNullOrWhiteSpace(shopOpsJobId)) return $"Job {shopOpsJobId}";
        if (!string.IsNullOrWhiteSpace(drawingId)) return string.IsNullOrWhiteSpace(drawingRevision) ? $"Drawing {drawingId}" : $"Drawing {drawingId} rev {drawingRevision}";
        if (!string.IsNullOrWhiteSpace(nestName)) return $"Nest {nestName}";
        if (!string.IsNullOrWhiteSpace(pepNestName)) return pepNestYear is null ? $"Nest {pepNestName}" : $"Nest {pepNestYear}: {pepNestName}";
        return "Unassigned material hold";
    }

    private sealed record ReservationProjection(long MaterialReservationId, long InventoryItemId, string PublicItemCode, string? MaterialDisplayName,
        string? LocationCode, string? ShopOpsJobId, string? DrawingId, string? DrawingRevision, int? PepNestYear,
        string? PepNestName, string? NestName, ReservationEquipment? Equipment, ReservationStatus Status, DateTime CreatedAtUtc, DateTime TimestampUtc);

}
