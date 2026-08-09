using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Receiving;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class ReceiveMaterialServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public ReceiveMaterialServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long locationId, long specId)> SeedAsync(
        MaterialLedgerDbContext db, InventoryLocationType locationType = InventoryLocationType.Shelf)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Programmer, IsActive = true, PasswordHash = "x" };
        var location = new InventoryLocation
        {
            LocationCode = $"LOC-{Guid.NewGuid():N}"[..30], DisplayName = "Slot", LocationType = locationType,
            StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true
        };
        var guid = Guid.NewGuid().ToString("N");
        var spec = new MaterialSpec { MaterialFamily = $"Steel-{guid}"[..30], Grade = $"Grade-{guid}"[..30], ThicknessIn = 0.25m, DisplayName = "Test Material", IsActive = true };
        db.AddRange(user, location, spec);
        await db.SaveChangesAsync();
        return (user.AppUserId, location.InventoryLocationId, spec.MaterialSpecId);
    }

    private static ReceiveMaterialRequest MakeRequest(long locationId, long specId, decimal quantity = 6m,
        string purchaseOrderNumber = "PO-1001", InventoryItemKind itemKind = InventoryItemKind.FullSheetBundle, bool isQuarantined = false) =>
        new(locationId, specId, itemKind, quantity, null, null, ShapeClassification.Rectangular, isQuarantined,
            "Acme Steel", purchaseOrderNumber, "RCPT-1", "VP-1", "HEAT-1", 42.50m, "Received via dock door 2");

    [Fact]
    public async Task ReceiveMaterialAsync_FullSheetBundle_CreatesLotItemAndTransaction()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId);

        var result = await service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId);

        var item = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == result.InventoryItemId);
        Assert.Equal(6m, item.QuantityOnHand);
        Assert.Equal(InventoryItemStatus.Available, item.Status);
        Assert.StartsWith("SHEET-", item.PublicItemCode);
        Assert.Equal(result.SheetLotId, item.SheetLotId);

        var lot = await db.SheetLots.SingleAsync(x => x.SheetLotId == result.SheetLotId);
        Assert.Equal(SheetLotSourceType.Receipt, lot.SourceType);
        Assert.Equal("PO-1001", lot.PurchaseOrderNumber);
        Assert.Equal("Acme Steel", lot.VendorName);
        Assert.Equal(42.50m, lot.CostPerUnit);

        var transactions = await db.MaterialTransactions.Where(x => x.InventoryItemId == item.InventoryItemId).ToListAsync();
        Assert.Single(transactions);
        Assert.Equal(MaterialTransactionType.Receipt, transactions[0].TransactionType);
        Assert.Equal(result.SheetLotId, transactions[0].SheetLotId);
        Assert.Equal(6m, transactions[0].QuantityDelta);
    }

    [Fact]
    public async Task ReceiveMaterialAsync_RejectsDropItemKind()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, quantity: 1m, itemKind: InventoryItemKind.Drop);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task ReceiveMaterialAsync_RejectsMissingPurchaseOrderNumber()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, purchaseOrderNumber: "   ");

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task ReceiveMaterialAsync_RejectsFractionalQuantity()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, quantity: 2.5m);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task ReceiveMaterialAsync_RejectsQuantityZero()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, quantity: 0m);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task ReceiveMaterialAsync_NonLeafLocation_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _, specId) = await SeedAsync(db);
        var rack = new InventoryLocation
        {
            LocationCode = $"RACK-{Guid.NewGuid():N}"[..30], DisplayName = "Rack", LocationType = InventoryLocationType.Rack,
            StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true
        };
        db.Add(rack);
        await db.SaveChangesAsync();
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(rack.InventoryLocationId, specId, quantity: 1m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task ReceiveMaterialAsync_QuarantinedItem_IsNotAvailable()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, isQuarantined: true);

        var result = await service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId);

        var item = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == result.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Quarantined, item.Status);
    }

    [Fact]
    public async Task ReceiveMaterialAsync_RetryWithSameCorrelationId_DoesNotDuplicate()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId);
        var correlationId = Guid.NewGuid();

        var first = await service.ReceiveMaterialAsync(correlationId, request, userId);
        var second = await service.ReceiveMaterialAsync(correlationId, request, userId);

        Assert.Equal(first.PublicItemCode, second.PublicItemCode);
        var itemCount = await db.InventoryItems.CountAsync(x => x.InventoryLocationId == locationId);
        Assert.Equal(1, itemCount);
        var lotCount = await db.SheetLots.CountAsync(x => x.MaterialSpecId == specId);
        Assert.Equal(1, lotCount);
    }
}
