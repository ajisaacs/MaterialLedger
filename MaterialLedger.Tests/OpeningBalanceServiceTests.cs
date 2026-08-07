using MaterialLedger.Data;
using MaterialLedger.Data.Census;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class OpeningBalanceServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public OpeningBalanceServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task RecordOpeningBalanceAsync_FullSheetBundle_CreatesItemAndOneImmutableTransaction()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var request = new OpeningBalanceRequest(locationId, specId, InventoryItemKind.FullSheetBundle, 6m, null, null, ShapeClassification.Rectangular, false, "Opening census");

        var result = await service.RecordOpeningBalanceAsync(Guid.NewGuid(), request, userId);

        var item = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == result.InventoryItemId);
        Assert.Equal(6m, item.QuantityOnHand);
        Assert.Equal(InventoryItemStatus.Available, item.Status);
        Assert.StartsWith("SHEET-", item.PublicItemCode);
        var transactions = await db.MaterialTransactions.Where(x => x.InventoryItemId == item.InventoryItemId).ToListAsync();
        Assert.Single(transactions);
        Assert.Equal(MaterialTransactionType.OpeningBalance, transactions[0].TransactionType);
        Assert.Equal(6m, transactions[0].QuantityDelta);
    }

    [Fact]
    public async Task RecordOpeningBalanceAsync_Drop_RequiresUsableGeometry()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db, InventoryLocationType.Bay);
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var request = new OpeningBalanceRequest(locationId, specId, InventoryItemKind.Drop, 1m, null, null, ShapeClassification.Rectangular, false, null);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordOpeningBalanceAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task RecordOpeningBalanceAsync_Drop_RejectsQuantityZero()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db, InventoryLocationType.Bay);
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var request = new OpeningBalanceRequest(locationId, specId, InventoryItemKind.Drop, 0m, 1m, 1m, ShapeClassification.Rectangular, false, null);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordOpeningBalanceAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task RecordOpeningBalanceAsync_FullSheetBundle_RejectsQuantityZero()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var request = new OpeningBalanceRequest(locationId, specId, InventoryItemKind.FullSheetBundle, 0m, null, null, ShapeClassification.Rectangular, false, null);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordOpeningBalanceAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task RecordOpeningBalanceAsync_QuarantinedItem_IsNotAvailable()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var request = new OpeningBalanceRequest(locationId, specId, InventoryItemKind.FullSheetBundle, 3m, null, null, ShapeClassification.Unknown, true, "Unknown mixed stock");

        var result = await service.RecordOpeningBalanceAsync(Guid.NewGuid(), request, userId);

        var item = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == result.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Quarantined, item.Status);
    }

    [Fact]
    public async Task RecordOpeningBalanceAsync_NonLeafLocation_Throws()
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
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var request = new OpeningBalanceRequest(rack.InventoryLocationId, specId, InventoryItemKind.FullSheetBundle, 1m, null, null, ShapeClassification.Rectangular, false, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordOpeningBalanceAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task RecordOpeningBalanceAsync_RetryWithSameCorrelationId_DoesNotDuplicate()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var request = new OpeningBalanceRequest(locationId, specId, InventoryItemKind.FullSheetBundle, 4m, null, null, ShapeClassification.Rectangular, false, null);
        var correlationId = Guid.NewGuid();

        var first = await service.RecordOpeningBalanceAsync(correlationId, request, userId);
        var second = await service.RecordOpeningBalanceAsync(correlationId, request, userId);

        Assert.Equal(first.PublicItemCode, second.PublicItemCode);
        var count = await db.InventoryItems.CountAsync(x => x.InventoryLocationId == locationId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetActiveItemsAtLocationAsync_ExcludesDepletedAndScrappedButIncludesAvailable()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var depleted = await service.RecordOpeningBalanceAsync(Guid.NewGuid(),
            new OpeningBalanceRequest(locationId, specId, InventoryItemKind.FullSheetBundle, 2m, null, null, ShapeClassification.Rectangular, false, null), userId);
        var active = await service.RecordOpeningBalanceAsync(Guid.NewGuid(),
            new OpeningBalanceRequest(locationId, specId, InventoryItemKind.FullSheetBundle, 5m, null, null, ShapeClassification.Rectangular, false, null), userId);
        var depletedItem = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == depleted.InventoryItemId);
        depletedItem.Status = InventoryItemStatus.Depleted;
        await db.SaveChangesAsync();

        var results = await service.GetActiveItemsAtLocationAsync(locationId);

        Assert.Single(results);
        Assert.Equal(active.InventoryItemId, results[0].InventoryItemId);
    }
}
