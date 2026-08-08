// MaterialLedger.Tests/ReservationServiceTests.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class ReservationServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public ReservationServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long slotId, long specId)> SeedAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Programmer, IsActive = true, PasswordHash = "x" };
        var slot = new InventoryLocation { LocationCode = $"SLOT-{Guid.NewGuid():N}"[..30], DisplayName = "Slot", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        var guid = Guid.NewGuid();
        var spec = new MaterialSpec { MaterialFamily = $"Carbon Steel-{guid}"[..30], Grade = $"A36-{guid}"[..30], ThicknessIn = 0.25m, DisplayName = "A36 1/4in", IsActive = true };
        db.AddRange(user, slot, spec);
        await db.SaveChangesAsync();
        return (user.AppUserId, slot.InventoryLocationId, spec.MaterialSpecId);
    }

    private static InventoryItem NewBundle(long slotId, long specId, int observedBy, decimal qty, DateTime? lastObservedAtUtc = null) => new()
    {
        PublicItemCode = $"SHEET-{Guid.NewGuid():N}"[..20],
        MaterialSpecId = specId,
        InventoryLocationId = slotId,
        ItemKind = InventoryItemKind.FullSheetBundle,
        Status = InventoryItemStatus.Available,
        QuantityOnHand = qty,
        ShapeClassification = ShapeClassification.Rectangular,
        LastObservedAtUtc = lastObservedAtUtc ?? DateTime.UtcNow,
        LastObservedBy = observedBy
    };

    private static InventoryItem NewDrop(long slotId, long specId, int observedBy, InventoryItemStatus status = InventoryItemStatus.Available) => new()
    {
        PublicItemCode = $"DROP-{Guid.NewGuid():N}"[..20],
        MaterialSpecId = specId,
        InventoryLocationId = slotId,
        ItemKind = InventoryItemKind.Drop,
        Status = status,
        QuantityOnHand = status == InventoryItemStatus.Depleted ? 0 : 1,
        UsableWidthIn = 12m,
        UsableLengthIn = 24m,
        ShapeClassification = ShapeClassification.Rectangular,
        LastObservedAtUtc = DateTime.UtcNow,
        LastObservedBy = observedBy
    };

    private static CreateReservationRequest RequestFor(long itemId) =>
        new(itemId, "JOB-1", "DWG-1", "A", ReservationEquipment.Laser, 2026, "NEST-1", null, null, null, null);

    [Fact]
    public async Task CreateReservationAsync_Available_CreatesProposedReservationAndTransaction()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);

        var result = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);

        var reservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == result.MaterialReservationId);
        Assert.Equal(ReservationStatus.Proposed, reservation.Status);
        Assert.Equal(1, reservation.ReservedQuantity);
        var txns = await db.MaterialTransactions.Where(x => x.MaterialReservationId == reservation.MaterialReservationId).ToListAsync();
        Assert.Single(txns);
        Assert.Equal(MaterialTransactionType.Reservation, txns[0].TransactionType);
        Assert.Equal(0m, txns[0].QuantityDelta);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Available, itemAfter.Status); // Create alone never claims availability
    }

    [Fact]
    public async Task CreateReservationAsync_QuarantinedItem_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId));
    }

    [Fact]
    public async Task CreateReservationAsync_StaleObservation_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 5m, DateTime.UtcNow.AddDays(-91));
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId));
    }

    [Fact]
    public async Task CreateReservationAsync_RetryWithSameCorrelationId_DoesNotDuplicate()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var correlationId = Guid.NewGuid();

        var first = await service.CreateReservationAsync(correlationId, RequestFor(item.InventoryItemId), userId);
        var second = await service.CreateReservationAsync(correlationId, RequestFor(item.InventoryItemId), userId);

        Assert.Equal(first.MaterialReservationId, second.MaterialReservationId);
        var count = await db.MaterialReservations.CountAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ApproveReservationAsync_DropAvailable_SetsApprovedAndReservesItem()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var created = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var reservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == created.MaterialReservationId);

        var result = await service.ApproveReservationAsync(Guid.NewGuid(), reservation.MaterialReservationId, reservation.RowVersion, userId);

        Assert.Equal(ReservationStatus.Approved, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Reserved, itemAfter.Status);
    }

    [Fact]
    public async Task ApproveReservationAsync_SecondApprovalForSameDrop_FailsTransactionally()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var first = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var second = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var firstReservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == first.MaterialReservationId);
        var secondReservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == second.MaterialReservationId);

        await service.ApproveReservationAsync(Guid.NewGuid(), firstReservation.MaterialReservationId, firstReservation.RowVersion, userId);

        await Assert.ThrowsAsync<ReservationBalanceException>(() =>
            service.ApproveReservationAsync(Guid.NewGuid(), secondReservation.MaterialReservationId, secondReservation.RowVersion, userId));
        var reservationRows = await db.MaterialReservations.Where(x => x.InventoryItemId == item.InventoryItemId).ToListAsync();
        Assert.Single(reservationRows, x => x.Status == ReservationStatus.Approved);
        Assert.Single(reservationRows, x => x.Status == ReservationStatus.Proposed);
    }

    [Fact]
    public async Task ApproveReservationAsync_BundleExceedingOnHand_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 1m);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var first = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var second = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var firstReservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == first.MaterialReservationId);
        var secondReservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == second.MaterialReservationId);
        await service.ApproveReservationAsync(Guid.NewGuid(), firstReservation.MaterialReservationId, firstReservation.RowVersion, userId);

        await Assert.ThrowsAsync<ReservationBalanceException>(() =>
            service.ApproveReservationAsync(Guid.NewGuid(), secondReservation.MaterialReservationId, secondReservation.RowVersion, userId));
    }

    [Fact]
    public async Task ApproveReservationAsync_StaleRowVersion_ThrowsConcurrencyException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var created = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var staleRowVersion = new byte[8];

        await Assert.ThrowsAsync<ReservationConcurrencyException>(() =>
            service.ApproveReservationAsync(Guid.NewGuid(), created.MaterialReservationId, staleRowVersion, userId));
    }

    [Fact]
    public async Task ApproveReservationAsync_AlreadyApproved_ThrowsInvalidOperation()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var created = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var reservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == created.MaterialReservationId);
        await service.ApproveReservationAsync(Guid.NewGuid(), reservation.MaterialReservationId, reservation.RowVersion, userId);
        var approved = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApproveReservationAsync(Guid.NewGuid(), approved.MaterialReservationId, approved.RowVersion, userId));
    }

    [Fact]
    public async Task ReleaseReservationAsync_Approved_RestoresItemAvailabilityAndWritesTransaction()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var created = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var proposed = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == created.MaterialReservationId);
        await service.ApproveReservationAsync(Guid.NewGuid(), proposed.MaterialReservationId, proposed.RowVersion, userId);
        var approved = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == created.MaterialReservationId);

        var result = await service.ReleaseReservationAsync(Guid.NewGuid(), approved.MaterialReservationId, approved.RowVersion, userId, "no longer needed");

        Assert.Equal(ReservationStatus.Released, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Available, itemAfter.Status);
        var releaseTxns = await db.MaterialTransactions.Where(x =>
            x.MaterialReservationId == approved.MaterialReservationId && x.TransactionType == MaterialTransactionType.ReservationRelease).ToListAsync();
        Assert.Single(releaseTxns);
    }

    [Fact]
    public async Task ReleaseReservationAsync_AwaitingCloseout_ThrowsInvalidOperation()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var created = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var proposed = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == created.MaterialReservationId);
        await service.ApproveReservationAsync(Guid.NewGuid(), proposed.MaterialReservationId, proposed.RowVersion, userId);
        var reservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == created.MaterialReservationId);
        reservation.Status = ReservationStatus.AwaitingCloseout;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReleaseReservationAsync(Guid.NewGuid(), reservation.MaterialReservationId, reservation.RowVersion, userId, null));
    }

    [Fact]
    public async Task ReleaseReservationAsync_StaleRowVersion_ThrowsConcurrencyException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var created = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var staleRowVersion = new byte[8];

        await Assert.ThrowsAsync<ReservationConcurrencyException>(() =>
            service.ReleaseReservationAsync(Guid.NewGuid(), created.MaterialReservationId, staleRowVersion, userId, null));
    }
}
