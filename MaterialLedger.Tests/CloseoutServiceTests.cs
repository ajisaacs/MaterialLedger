using MaterialLedger.Data;
using MaterialLedger.Data.Closeout;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class CloseoutServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public CloseoutServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long slotId, long destinationSlotId, long specId, long lotId)> SeedAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Operator, IsActive = true, PasswordHash = "x" };
        var slot = new InventoryLocation { LocationCode = $"SLOT-{Guid.NewGuid():N}"[..30], DisplayName = "Slot", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        var destinationSlot = new InventoryLocation { LocationCode = $"DEST-{Guid.NewGuid():N}"[..30], DisplayName = "Destination", LocationType = InventoryLocationType.Bay, StorageMode = InventoryStorageMode.VerticalDropBay, IsActive = true };
        var guid = Guid.NewGuid();
        var spec = new MaterialSpec { MaterialFamily = $"Carbon Steel-{guid}"[..30], Grade = $"A36-{guid}"[..30], ThicknessIn = 0.25m, DisplayName = "A36 1/4in", IsActive = true };
        db.AddRange(user, slot, destinationSlot, spec);
        await db.SaveChangesAsync();
        var lot = new SheetLot { MaterialSpecId = spec.MaterialSpecId, SourceType = SheetLotSourceType.OpeningBalance, HeatLotNumber = "HEAT-1" };
        db.SheetLots.Add(lot);
        await db.SaveChangesAsync();
        return (user.AppUserId, slot.InventoryLocationId, destinationSlot.InventoryLocationId, spec.MaterialSpecId, lot.SheetLotId);
    }

    private static InventoryItem NewBundle(long slotId, long specId, int observedBy, decimal qty, long? lotId = null) => new()
    {
        PublicItemCode = $"SHEET-{Guid.NewGuid():N}"[..20],
        MaterialSpecId = specId,
        SheetLotId = lotId,
        InventoryLocationId = slotId,
        ItemKind = InventoryItemKind.FullSheetBundle,
        Status = InventoryItemStatus.Available,
        QuantityOnHand = qty,
        ShapeClassification = ShapeClassification.Rectangular,
        LastObservedAtUtc = DateTime.UtcNow,
        LastObservedBy = observedBy
    };

    private static InventoryItem NewDrop(long slotId, long specId, int observedBy, long? lotId = null) => new()
    {
        PublicItemCode = $"DROP-{Guid.NewGuid():N}"[..20],
        MaterialSpecId = specId,
        SheetLotId = lotId,
        InventoryLocationId = slotId,
        ItemKind = InventoryItemKind.Drop,
        Status = InventoryItemStatus.Reserved,
        QuantityOnHand = 1,
        UsableWidthIn = 12m,
        UsableLengthIn = 24m,
        ShapeClassification = ShapeClassification.Rectangular,
        LastObservedAtUtc = DateTime.UtcNow,
        LastObservedBy = observedBy
    };

    private async Task<MaterialReservation> ApprovedReservationForAsync(MaterialLedgerDbContext db, long itemId, int userId, ReservationStatus status = ReservationStatus.Approved)
    {
        var reservation = new MaterialReservation
        {
            InventoryItemId = itemId,
            ReservedQuantity = 1,
            Status = status,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = userId,
            ApprovedAtUtc = DateTime.UtcNow,
            ApprovedBy = userId
        };
        db.MaterialReservations.Add(reservation);
        await db.SaveChangesAsync();
        return reservation;
    }

    private static CloseoutConsumptionRequest RequestFor(long reservationId, byte[] rowVersion, ProductionConsumptionOutcome outcome,
        decimal? width = null, decimal? length = null, long? destinationId = null, string? notes = null) =>
        new(reservationId, rowVersion, outcome, width, length, destinationId, notes);

    [Fact]
    public async Task MarkReservationAwaitingCloseoutAsync_Approved_TransitionsToAwaitingCloseout()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.MarkReservationAwaitingCloseoutAsync(Guid.NewGuid(), reservation.MaterialReservationId, reservation.RowVersion, userId);

        Assert.Equal(ReservationStatus.AwaitingCloseout, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(InventoryItemStatus.AwaitingCloseout, itemAfter.Status);
    }

    [Fact]
    public async Task MarkReservationAwaitingCloseoutAsync_Bundle_DoesNotChangeItemStatus()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, lotId) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 3m, lotId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.MarkReservationAwaitingCloseoutAsync(Guid.NewGuid(), reservation.MaterialReservationId, reservation.RowVersion, userId);

        Assert.Equal(ReservationStatus.AwaitingCloseout, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Available, itemAfter.Status);
    }

    [Fact]
    public async Task MarkReservationAwaitingCloseoutAsync_NotApproved_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId, ReservationStatus.Proposed);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MarkReservationAwaitingCloseoutAsync(Guid.NewGuid(), reservation.MaterialReservationId, reservation.RowVersion, userId));
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_AllConsumed_Bundle_DecrementsQuantityWithoutForcingDepleted()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, lotId) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 3m, lotId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.CloseoutConsumptionAsync(Guid.NewGuid(),
            RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.AllConsumed), userId);

        Assert.Equal(ReservationStatus.ClosedConsumed, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(2m, itemAfter.QuantityOnHand);
        Assert.Equal(InventoryItemStatus.Available, itemAfter.Status);
        var txn = await db.MaterialTransactions.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(MaterialTransactionType.Consumption, txn.TransactionType);
        Assert.Equal(-1m, txn.QuantityDelta);
        Assert.Equal(lotId, txn.SheetLotId);
        var consumption = await db.ProductionConsumptions.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(ProductionConsumptionOutcome.AllConsumed, consumption.Outcome);
        Assert.Equal(txn.MaterialTransactionId, consumption.PrimaryMaterialTransactionId);
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_AllConsumed_Drop_DepletesItemAndClosesReservation()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.CloseoutConsumptionAsync(Guid.NewGuid(),
            RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.AllConsumed), userId);

        Assert.Equal(ReservationStatus.ClosedConsumed, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(0m, itemAfter.QuantityOnHand);
        Assert.Equal(InventoryItemStatus.Depleted, itemAfter.Status);
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_FullSheetReturned_DropReturnsToAvailableWithNoQuantityChange()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.CloseoutConsumptionAsync(Guid.NewGuid(),
            RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.FullSheetReturned), userId);

        Assert.Equal(ReservationStatus.ClosedReturned, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(1m, itemAfter.QuantityOnHand);
        Assert.Equal(InventoryItemStatus.Available, itemAfter.Status);
        var txn = await db.MaterialTransactions.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(MaterialTransactionType.Return, txn.TransactionType);
        Assert.Equal(0m, txn.QuantityDelta);
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_UsableDropCreated_CreatesRemnantInheritingSheetLotAtDestination()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, destinationSlotId, specId, lotId) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 2m, lotId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.CloseoutConsumptionAsync(Guid.NewGuid(),
            RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.UsableDropCreated,
                width: 10m, length: 15m, destinationId: destinationSlotId), userId);

        Assert.Equal(ReservationStatus.ClosedConsumed, result.Status);
        Assert.NotNull(result.RemnantPublicItemCode);
        var sourceAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(1m, sourceAfter.QuantityOnHand);
        var remnant = await db.InventoryItems.SingleAsync(x => x.PublicItemCode == result.RemnantPublicItemCode);
        Assert.Equal(InventoryItemKind.Drop, remnant.ItemKind);
        Assert.Equal(1m, remnant.QuantityOnHand);
        Assert.Equal(10m, remnant.UsableWidthIn);
        Assert.Equal(15m, remnant.UsableLengthIn);
        Assert.Equal(destinationSlotId, remnant.InventoryLocationId);
        Assert.Equal(lotId, remnant.SheetLotId);
        Assert.Equal(InventoryItemStatus.Available, remnant.Status);
        var remnantTxn = await db.MaterialTransactions.SingleAsync(x => x.TransactionType == MaterialTransactionType.RemnantCreated && x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(remnant.InventoryItemId, remnantTxn.InventoryItemId);
        Assert.Equal(item.InventoryItemId, remnantTxn.RelatedInventoryItemId);
        Assert.Equal(reservation.MaterialReservationId, remnantTxn.MaterialReservationId);
        var consumeTxn = await db.MaterialTransactions.SingleAsync(x => x.TransactionType == MaterialTransactionType.Consumption && x.MaterialReservationId == reservation.MaterialReservationId);
        var consumption = await db.ProductionConsumptions.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(consumeTxn.MaterialTransactionId, consumption.PrimaryMaterialTransactionId);
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_UsableDropCreated_MissingMeasurements_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, destinationSlotId, specId, _) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 2m);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CloseoutConsumptionAsync(Guid.NewGuid(),
                RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.UsableDropCreated,
                    destinationId: destinationSlotId), userId));
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_UsableDropCreated_NonLeafDestination_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 2m);
        db.InventoryItems.Add(item);
        var areaLocation = new InventoryLocation { LocationCode = $"AREA-{Guid.NewGuid():N}"[..30], DisplayName = "Area", LocationType = InventoryLocationType.Area, StorageMode = InventoryStorageMode.Other, IsActive = true };
        db.InventoryLocations.Add(areaLocation);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CloseoutConsumptionAsync(Guid.NewGuid(),
                RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.UsableDropCreated,
                    width: 10m, length: 15m, destinationId: areaLocation.InventoryLocationId), userId));
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_NeedsReview_QuarantinesItemAndOpensException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.CloseoutConsumptionAsync(Guid.NewGuid(),
            RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.NeedsReview, notes: "unsure what happened"), userId);

        Assert.Equal(ReservationStatus.Exception, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Quarantined, itemAfter.Status);
        var exception = await db.MaterialExceptions.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(MaterialExceptionType.OutcomeUnknown, exception.ExceptionType);
        Assert.Equal(MaterialExceptionStatus.Open, exception.Status);
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_FromAwaitingCloseoutStatus_Succeeds()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId, ReservationStatus.AwaitingCloseout);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.CloseoutConsumptionAsync(Guid.NewGuid(),
            RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.AllConsumed), userId);

        Assert.Equal(ReservationStatus.ClosedConsumed, result.Status);
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_ReservationProposed_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId, ReservationStatus.Proposed);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CloseoutConsumptionAsync(Guid.NewGuid(),
                RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.AllConsumed), userId));
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_StaleRowVersion_ThrowsConcurrencyException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));
        var staleRowVersion = new byte[8];

        await Assert.ThrowsAsync<ReservationConcurrencyException>(() =>
            service.CloseoutConsumptionAsync(Guid.NewGuid(),
                RequestFor(reservation.MaterialReservationId, staleRowVersion, ProductionConsumptionOutcome.AllConsumed), userId));
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_RetryWithSameCorrelationId_DoesNotDuplicate()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));
        var correlationId = Guid.NewGuid();
        var request = RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.AllConsumed);

        var first = await service.CloseoutConsumptionAsync(correlationId, request, userId);
        var second = await service.CloseoutConsumptionAsync(correlationId, request, userId);

        Assert.Equal(first.Status, second.Status);
        var count = await db.ProductionConsumptions.CountAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(1, count);
        var txnCount = await db.MaterialTransactions.CountAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(1, txnCount);
    }
}
