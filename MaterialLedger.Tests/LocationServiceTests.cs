using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Locations;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class LocationServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public LocationServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<int> SeedUserAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
        db.Add(user);
        await db.SaveChangesAsync();
        return user.AppUserId;
    }

    private async Task<InventoryLocation> SeedAreaAsync(MaterialLedgerDbContext db, int sortOrder = 0)
    {
        var area = new InventoryLocation
        {
            LocationCode = $"AREA-{Guid.NewGuid():N}"[..20], DisplayName = "Area", LocationType = InventoryLocationType.Area,
            StorageMode = InventoryStorageMode.Other, SortOrder = sortOrder, IsActive = true
        };
        db.Add(area);
        await db.SaveChangesAsync();
        return area;
    }

    [Fact]
    public async Task CreateLocationAsync_Area_NoParent_Succeeds()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        var request = new CreateLocationRequest("new-area", "New Area", InventoryLocationType.Area, InventoryStorageMode.Other, null);
        var result = await service.CreateLocationAsync(Guid.NewGuid(), request, userId);

        Assert.Equal("NEW-AREA", result.LocationCode);
        Assert.Null(result.ParentInventoryLocationId);
        Assert.False(result.CapacityConfirmed);
        Assert.True(result.IsActive);
    }

    [Fact]
    public async Task CreateLocationAsync_RackUnderExistingArea_PersistsStorageModeAndParent()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var area = await SeedAreaAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        var request = new CreateLocationRequest("new-rack", "New Rack", InventoryLocationType.Rack, InventoryStorageMode.VerticalDropBay, area.InventoryLocationId);
        var result = await service.CreateLocationAsync(Guid.NewGuid(), request, userId);

        Assert.Equal(area.InventoryLocationId, result.ParentInventoryLocationId);
        Assert.Equal(InventoryStorageMode.VerticalDropBay, result.StorageMode);
    }

    [Fact]
    public async Task CreateLocationAsync_LowercaseCode_IsNormalizedToUppercase()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        var result = await service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("  mixed-Case  ", "Name", InventoryLocationType.Area, InventoryStorageMode.Other, null), userId);

        Assert.Equal("MIXED-CASE", result.LocationCode);
    }

    [Fact]
    public async Task CreateLocationAsync_CodeWithInvalidCharacters_Throws()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("BAD CODE!", "Name", InventoryLocationType.Area, InventoryStorageMode.Other, null), userId));
    }

    [Fact]
    public async Task CreateLocationAsync_DuplicateCode_Throws()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));
        await service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("DUP-CODE", "Name", InventoryLocationType.Area, InventoryStorageMode.Other, null), userId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("dup-code", "Name Again", InventoryLocationType.Area, InventoryStorageMode.Other, null), userId));
    }

    [Fact]
    public async Task CreateLocationAsync_RackWithNoParent_Throws()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("ORPHAN-RACK", "Name", InventoryLocationType.Rack, InventoryStorageMode.Other, null), userId));
    }

    [Fact]
    public async Task CreateLocationAsync_RackWithNonAreaParent_Throws()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var area = await SeedAreaAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));
        var rack = await service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("PARENT-RACK", "Rack", InventoryLocationType.Rack, InventoryStorageMode.Other, area.InventoryLocationId), userId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("CHILD-RACK", "Name", InventoryLocationType.Rack, InventoryStorageMode.Other, rack.InventoryLocationId), userId));
    }

    [Fact]
    public async Task CreateLocationAsync_AreaWithParentSpecified_Throws()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var area = await SeedAreaAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("CHILD-AREA", "Name", InventoryLocationType.Area, InventoryStorageMode.Other, area.InventoryLocationId), userId));
    }

    [Fact]
    public async Task CreateLocationAsync_SortOrder_IsMaxSiblingPlusOne()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var area = await SeedAreaAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        var firstRack = await service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("RACK-A", "Rack A", InventoryLocationType.Rack, InventoryStorageMode.Other, area.InventoryLocationId), userId);
        var secondRack = await service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("RACK-B", "Rack B", InventoryLocationType.Rack, InventoryStorageMode.Other, area.InventoryLocationId), userId);

        Assert.Equal(0, firstRack.SortOrder);
        Assert.Equal(1, secondRack.SortOrder);
    }

    [Fact]
    public async Task CreateLocationAsync_RetryWithSameCorrelationId_ReturnsReplayWithoutDuplicating()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));
        var correlationId = Guid.NewGuid();
        var request = new CreateLocationRequest("REPLAY-AREA", "Name", InventoryLocationType.Area, InventoryStorageMode.Other, null);

        var first = await service.CreateLocationAsync(correlationId, request, userId);
        var second = await service.CreateLocationAsync(correlationId, request, userId);

        Assert.Equal(first.InventoryLocationId, second.InventoryLocationId);
        var count = await db.InventoryLocations.CountAsync(x => x.LocationCode == "REPLAY-AREA");
        Assert.Equal(1, count);
    }
}
