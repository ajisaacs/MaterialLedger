using MaterialLedger.Data;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class WorkQueueSummaryContractTests
{
    [Fact]
    public void WorkQueueSummary_exposes_scalar_control_board_counts_without_queue_collections()
    {
        var summaryType = typeof(WorkQueueSummary);

        Assert.True(summaryType.GetProperty(nameof(WorkQueueSummary.ApprovalNeededCount))!.CanWrite);
        Assert.True(summaryType.GetProperty(nameof(WorkQueueSummary.ReadyForCuttingCount))!.CanWrite);
        Assert.True(summaryType.GetProperty(nameof(WorkQueueSummary.CutResultNeededCount))!.CanWrite);
        Assert.True(summaryType.GetProperty(nameof(WorkQueueSummary.AttentionNeededCount))!.CanWrite);
        Assert.Null(summaryType.GetProperty("ApprovalNeeded"));
        Assert.Null(summaryType.GetProperty("ReadyForCutting"));
        Assert.Null(summaryType.GetProperty("CutResultNeeded"));
        Assert.Null(summaryType.GetProperty("AttentionItems"));
    }
}

[Trait("Category", "Integration")]
public class WorkQueueQueryServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public WorkQueueQueryServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetAsync_counts_proposed_work_and_all_cutting_actions_for_the_control_board()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, "WQ-STATES", 4m);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        var proposedLaser = NewReservation(item.InventoryItemId, userId, ReservationStatus.Proposed, ReservationEquipment.Laser, "JOB-PROPOSED-LASER");
        var proposedPlasma = NewReservation(item.InventoryItemId, userId, ReservationStatus.Proposed, ReservationEquipment.Plasma, "JOB-PROPOSED-PLASMA");
        var readyLaser = NewReservation(item.InventoryItemId, userId, ReservationStatus.Approved, ReservationEquipment.Laser, "JOB-READY");
        var readyPlasma = NewReservation(item.InventoryItemId, userId, ReservationStatus.Approved, ReservationEquipment.Plasma, "JOB-PLASMA");
        var closeoutLaser = NewReservation(item.InventoryItemId, userId, ReservationStatus.AwaitingCloseout, ReservationEquipment.Laser, "JOB-CLOSEOUT");
        var closeoutPlasma = NewReservation(item.InventoryItemId, userId, ReservationStatus.AwaitingCloseout, ReservationEquipment.Plasma, "JOB-CLOSEOUT-PLASMA");
        var terminalLaser = NewReservation(item.InventoryItemId, userId, ReservationStatus.ClosedConsumed, ReservationEquipment.Laser, "JOB-CLOSED");
        db.MaterialReservations.AddRange(proposedLaser, proposedPlasma, readyLaser, readyPlasma, closeoutLaser, closeoutPlasma, terminalLaser);
        await db.SaveChangesAsync();

        var createdReservationIds = new[]
        {
            proposedLaser.MaterialReservationId,
            proposedPlasma.MaterialReservationId,
            readyLaser.MaterialReservationId,
            readyPlasma.MaterialReservationId,
            closeoutLaser.MaterialReservationId,
            closeoutPlasma.MaterialReservationId,
            terminalLaser.MaterialReservationId
        };
        var persistedCreatedReservations = await db.MaterialReservations
            .Where(reservation => createdReservationIds.Contains(reservation.MaterialReservationId))
            .ToListAsync();

        var approvalMatches = persistedCreatedReservations
            .Where(reservation => reservation.Status == ReservationStatus.Proposed)
            .Select(reservation => reservation.MaterialReservationId)
            .OrderBy(id => id)
            .ToArray();
        var readyMatches = persistedCreatedReservations
            .Where(reservation => reservation.Status == ReservationStatus.Approved)
            .Select(reservation => reservation.MaterialReservationId)
            .OrderBy(id => id)
            .ToArray();
        var cutResultMatches = persistedCreatedReservations
            .Where(reservation => reservation.Status == ReservationStatus.AwaitingCloseout)
            .Select(reservation => reservation.MaterialReservationId)
            .OrderBy(id => id)
            .ToArray();

        Assert.Equal(new[] { proposedLaser.MaterialReservationId, proposedPlasma.MaterialReservationId }.OrderBy(id => id), approvalMatches);
        Assert.Equal(new[] { readyLaser.MaterialReservationId, readyPlasma.MaterialReservationId }.OrderBy(id => id), readyMatches);
        Assert.Equal(new[] { closeoutLaser.MaterialReservationId, closeoutPlasma.MaterialReservationId }.OrderBy(id => id), cutResultMatches);
        Assert.Contains(persistedCreatedReservations, reservation => reservation.MaterialReservationId == proposedLaser.MaterialReservationId && reservation.Status == ReservationStatus.Proposed && reservation.Equipment == ReservationEquipment.Laser);
        Assert.Contains(persistedCreatedReservations, reservation => reservation.MaterialReservationId == proposedPlasma.MaterialReservationId && reservation.Status == ReservationStatus.Proposed && reservation.Equipment == ReservationEquipment.Plasma);
        Assert.DoesNotContain(persistedCreatedReservations, reservation => reservation.MaterialReservationId == terminalLaser.MaterialReservationId && reservation.Status == ReservationStatus.Proposed);
        Assert.DoesNotContain(persistedCreatedReservations, reservation => reservation.MaterialReservationId == terminalLaser.MaterialReservationId && reservation.Status == ReservationStatus.Approved && reservation.Equipment == ReservationEquipment.Laser);
        Assert.DoesNotContain(persistedCreatedReservations, reservation => reservation.MaterialReservationId == terminalLaser.MaterialReservationId && reservation.Status == ReservationStatus.AwaitingCloseout);

        var expectedApprovalCount = await db.MaterialReservations
            .Where(reservation => reservation.Status == ReservationStatus.Proposed)
            .CountAsync();
        var expectedReadyForCuttingCount = await db.MaterialReservations
            .Where(reservation => reservation.Status == ReservationStatus.Approved)
            .CountAsync();
        var expectedCutResultNeededCount = await db.MaterialReservations
            .Where(reservation => reservation.Status == ReservationStatus.AwaitingCloseout)
            .CountAsync();

        var summary = await CreateService(db).GetAsync();

        Assert.Equal(expectedApprovalCount, summary.ApprovalNeededCount);
        Assert.Equal(expectedReadyForCuttingCount, summary.ReadyForCuttingCount);
        Assert.Equal(expectedCutResultNeededCount, summary.CutResultNeededCount);
    }

    [Fact]
    public async Task GetAsync_limits_recent_work_to_ten_rows_and_orders_equal_timestamps_by_reservation_id_descending()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, "WQ-RECENT", 12m);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        var timestamp = new DateTime(2100, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var reservations = Enumerable.Range(0, 11)
            .Select(index => NewReservation(item.InventoryItemId, userId, ReservationStatus.ClosedConsumed, ReservationEquipment.Laser, $"JOB-RECENT-{index:00}", timestamp))
            .ToArray();
        db.MaterialReservations.AddRange(reservations);
        await db.SaveChangesAsync();

        var summary = await CreateService(db).GetAsync();
        var createdIds = reservations.Select(reservation => reservation.MaterialReservationId).ToHashSet();
        var recentCreatedIds = summary.RecentWork
            .Where(work => createdIds.Contains(work.MaterialReservationId))
            .Select(work => work.MaterialReservationId)
            .ToArray();
        var expected = reservations
            .OrderByDescending(reservation => reservation.MaterialReservationId)
            .Take(10)
            .Select(reservation => reservation.MaterialReservationId)
            .ToArray();

        Assert.Equal(expected, recentCreatedIds);
        Assert.DoesNotContain(reservations.OrderBy(reservation => reservation.MaterialReservationId).First().MaterialReservationId, recentCreatedIds);
    }

    [Fact]
    public async Task GetAsync_uses_inventory_available_only_rules_for_available_material_count()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var available = NewItem(slotId, specId, userId, "WQ-AVAILABLE", 1m);
        var quarantined = NewItem(slotId, specId, userId, "WQ-QUARANTINED", 1m);
        quarantined.Status = InventoryItemStatus.Quarantined;
        var stale = NewItem(slotId, specId, userId, "WQ-STALE", 1m);
        stale.LastObservedAtUtc = DateTime.UtcNow.AddDays(-91);
        var fullyHeld = NewItem(slotId, specId, userId, "WQ-HELD", 1m);
        db.InventoryItems.AddRange(available, quarantined, stale, fullyHeld);
        await db.SaveChangesAsync();
        db.MaterialReservations.Add(NewReservation(fullyHeld.InventoryItemId, userId, ReservationStatus.Approved, ReservationEquipment.Laser, "JOB-HELD"));
        await db.SaveChangesAsync();

        var summary = await CreateService(db).GetAsync();
        var availableIds = await new InventoryQueryService(db, freshnessThresholdDays: 90)
            .SearchAsync(new InventoryFilter(null, null, null, null, null, null, null, null, AvailableOnly: true));

        Assert.Equal(availableIds.Count, summary.AvailableMaterialCount);
        Assert.Contains(availableIds, item => item.InventoryItemId == available.InventoryItemId);
        Assert.DoesNotContain(availableIds, item => item.InventoryItemId == quarantined.InventoryItemId || item.InventoryItemId == stale.InventoryItemId || item.InventoryItemId == fullyHeld.InventoryItemId);
    }

    private static WorkQueueQueryService CreateService(MaterialLedgerDbContext db) =>
        new(db, new InventoryQueryService(db, freshnessThresholdDays: 90));

    private static async Task<(int userId, long slotId, long specId)> SeedAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"wq_{Guid.NewGuid():N}", DisplayName = "Queue tester", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
        var slot = new InventoryLocation { LocationCode = $"SLOT-{Guid.NewGuid():N}"[..30], DisplayName = "Queue slot", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        var spec = new MaterialSpec { MaterialFamily = $"Test-{Guid.NewGuid():N}"[..30], Grade = "A36", ThicknessIn = 0.25m, DisplayName = "Test steel", IsActive = true };
        db.AddRange(user, slot, spec);
        await db.SaveChangesAsync();
        return (user.AppUserId, slot.InventoryLocationId, spec.MaterialSpecId);
    }

    private static InventoryItem NewItem(long slotId, long specId, int userId, string prefix, decimal quantity) => new()
    {
        PublicItemCode = $"{prefix}-{Guid.NewGuid():N}"[..30], MaterialSpecId = specId, InventoryLocationId = slotId,
        ItemKind = InventoryItemKind.FullSheetBundle, Status = InventoryItemStatus.Available, QuantityOnHand = quantity,
        ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = userId
    };

    private static MaterialReservation NewReservation(long itemId, int userId, ReservationStatus status, ReservationEquipment equipment, string jobId, DateTime? timestamp = null) => new()
    {
        InventoryItemId = itemId, ReservedQuantity = 1, Status = status, Equipment = equipment, ShopOpsJobId = jobId,
        CreatedAtUtc = timestamp ?? DateTime.UtcNow, CreatedBy = userId,
        ApprovedAtUtc = status is ReservationStatus.Proposed ? null : timestamp ?? DateTime.UtcNow,
        ApprovedBy = status is ReservationStatus.Proposed ? null : userId
    };
}
