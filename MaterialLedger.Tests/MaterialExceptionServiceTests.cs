using MaterialLedger.Data;
using MaterialLedger.Data.Exceptions;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class MaterialExceptionServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public MaterialExceptionServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long slotId, long specId)> SeedAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
        var slot = new InventoryLocation { LocationCode = $"SLOT-{Guid.NewGuid():N}"[..30], DisplayName = "Slot", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        var guid = Guid.NewGuid();
        var spec = new MaterialSpec { MaterialFamily = $"Carbon Steel-{guid}"[..30], Grade = $"A36-{guid}"[..30], ThicknessIn = 0.25m, DisplayName = "A36 1/4in", IsActive = true };
        db.AddRange(user, slot, spec);
        await db.SaveChangesAsync();
        return (user.AppUserId, slot.InventoryLocationId, spec.MaterialSpecId);
    }

    private static InventoryItem NewItem(long slotId, long specId, int observedBy, InventoryItemStatus status, DateTime? lastObservedAtUtc = null) => new()
    {
        PublicItemCode = $"SHEET-{Guid.NewGuid():N}"[..20],
        MaterialSpecId = specId,
        InventoryLocationId = slotId,
        ItemKind = InventoryItemKind.FullSheetBundle,
        Status = status,
        QuantityOnHand = 3m,
        ShapeClassification = ShapeClassification.Rectangular,
        LastObservedAtUtc = lastObservedAtUtc ?? DateTime.UtcNow,
        LastObservedBy = observedBy
    };

    [Fact]
    public async Task ScanAsync_QuarantinedItem_OpensQuarantinedMaterialException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);

        var result = await service.ScanAsync();

        Assert.Equal(1, result.OpenedCount);
        var exception = await db.MaterialExceptions.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(MaterialExceptionType.QuarantinedMaterial, exception.ExceptionType);
        Assert.Equal(MaterialExceptionStatus.Open, exception.Status);
    }

    [Fact]
    public async Task ScanAsync_StaleItem_OpensStaleObservationException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Available, DateTime.UtcNow.AddDays(-91));
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);

        await service.ScanAsync();

        var exception = await db.MaterialExceptions.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(MaterialExceptionType.StaleObservation, exception.ExceptionType);
    }

    [Fact]
    public async Task ScanAsync_OverdueAwaitingCloseoutReservation_OpensAwaitingCloseoutOverdueException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Available);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = new MaterialReservation
        {
            InventoryItemId = item.InventoryItemId,
            ReservedQuantity = 1,
            Status = ReservationStatus.AwaitingCloseout,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-3),
            CreatedBy = userId,
            ApprovedAtUtc = DateTime.UtcNow.AddDays(-3),
            ApprovedBy = userId
        };
        db.MaterialReservations.Add(reservation);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);

        await service.ScanAsync();

        var exception = await db.MaterialExceptions.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(MaterialExceptionType.AwaitingCloseoutOverdue, exception.ExceptionType);
    }

    [Fact]
    public async Task ScanAsync_ConditionAlreadyOpen_DoesNotOpenSecondRow()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);

        await service.ScanAsync();
        var secondScan = await service.ScanAsync();

        Assert.Equal(0, secondScan.OpenedCount);
        var count = await db.MaterialExceptions.CountAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ScanAsync_ConditionRecursAfterResolution_OpensNewRowPreservingOld()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);
        await service.ScanAsync();
        var firstException = await db.MaterialExceptions.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        await service.ResolveMaterialExceptionAsync(Guid.NewGuid(), firstException.MaterialExceptionId, "reviewed", userId);

        var secondScan = await service.ScanAsync();

        Assert.Equal(1, secondScan.OpenedCount);
        var allExceptions = await db.MaterialExceptions.Where(x => x.InventoryItemId == item.InventoryItemId).ToListAsync();
        Assert.Equal(2, allExceptions.Count);
        Assert.Single(allExceptions, x => x.Status == MaterialExceptionStatus.Fixed);
        Assert.Single(allExceptions, x => x.Status == MaterialExceptionStatus.Open);
    }

    [Fact]
    public async Task ResolveMaterialExceptionAsync_OpenException_MarksFixedWithActorAndNotes()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);
        await service.ScanAsync();
        var exception = await db.MaterialExceptions.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);

        var resolved = await service.ResolveMaterialExceptionAsync(Guid.NewGuid(), exception.MaterialExceptionId, "operator re-identified the material", userId);

        Assert.Equal(MaterialExceptionStatus.Fixed, resolved.Status);
        Assert.Equal(userId, resolved.ResolvedBy);
        Assert.Equal("operator re-identified the material", resolved.ResolutionNotes);
        Assert.NotNull(resolved.ResolvedAtUtc);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Quarantined, itemAfter.Status); // resolving does not auto-revert item state
        await service.ScanAsync();
    }

    [Fact]
    public async Task ResolveMaterialExceptionAsync_AlreadyResolved_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);
        await service.ScanAsync();
        var exception = await db.MaterialExceptions.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        await service.ResolveMaterialExceptionAsync(Guid.NewGuid(), exception.MaterialExceptionId, "first resolution", userId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResolveMaterialExceptionAsync(Guid.NewGuid(), exception.MaterialExceptionId, "second resolution", userId));
        await service.ScanAsync();
    }

    [Fact]
    public async Task GetOpenExceptionsAsync_ReturnsOnlyOpenRows()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);
        await service.ScanAsync();
        var exception = await db.MaterialExceptions.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        await service.ResolveMaterialExceptionAsync(Guid.NewGuid(), exception.MaterialExceptionId, "resolved", userId);
        await service.ScanAsync();

        var open = await service.GetOpenExceptionsAsync();
        var resolved = await service.GetRecentlyResolvedAsync();

        Assert.DoesNotContain(open, x => x.MaterialExceptionId == exception.MaterialExceptionId);
        Assert.Contains(resolved, x => x.MaterialExceptionId == exception.MaterialExceptionId);
    }
}
