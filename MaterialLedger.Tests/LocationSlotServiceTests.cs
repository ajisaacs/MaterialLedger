using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Locations;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class LocationSlotServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public LocationSlotServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, string rackCode)> SeedRackAsync(MaterialLedgerDbContext db, InventoryStorageMode mode)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
        var rackCode = $"RACK-{Guid.NewGuid():N}"[..20];
        var rack = new InventoryLocation
        {
            LocationCode = rackCode, DisplayName = "Test Rack", LocationType = InventoryLocationType.Rack,
            StorageMode = mode, SortOrder = 1, CapacityConfirmed = false, IsActive = true
        };
        db.AddRange(user, rack);
        await db.SaveChangesAsync();
        return (user.AppUserId, rackCode);
    }

    [Fact]
    public async Task ConfirmLocationSlotsAsync_RolloutRack_CreatesShelvesAndMarksParentConfirmed()
    {
        await using var db = _fixture.CreateContext();
        var (userId, rackCode) = await SeedRackAsync(db, InventoryStorageMode.RolloutShelf);
        var service = new LocationSlotService(db, new OperationLogGuard(db));
        var slots = new[] { new SlotDefinition("S01", "Shelf 1", 1), new SlotDefinition("S02", "Shelf 2", 2) };

        var result = await service.ConfirmLocationSlotsAsync(Guid.NewGuid(), rackCode, slots, userId);

        Assert.Equal(2, result.CreatedLocationCodes.Count);
        var created = await db.InventoryLocations.Where(x => x.ParentInventoryLocationId == result.ParentInventoryLocationId).ToListAsync();
        Assert.All(created, x => Assert.Equal(InventoryLocationType.Shelf, x.LocationType));
        var parent = await db.InventoryLocations.FirstAsync(x => x.LocationCode == rackCode);
        Assert.True(parent.CapacityConfirmed);
    }

    [Fact]
    public async Task ConfirmLocationSlotsAsync_DropRack_CreatesBays()
    {
        await using var db = _fixture.CreateContext();
        var (userId, rackCode) = await SeedRackAsync(db, InventoryStorageMode.VerticalDropBay);
        var service = new LocationSlotService(db, new OperationLogGuard(db));

        var result = await service.ConfirmLocationSlotsAsync(Guid.NewGuid(), rackCode, [new SlotDefinition("B01", "Bay 1", 1)], userId);

        var created = await db.InventoryLocations.Where(x => x.ParentInventoryLocationId == result.ParentInventoryLocationId).ToListAsync();
        Assert.All(created, x => Assert.Equal(InventoryLocationType.Bay, x.LocationType));
    }

    [Fact]
    public async Task ConfirmLocationSlotsAsync_SkidRack_CreatesStagingPositions()
    {
        await using var db = _fixture.CreateContext();
        var (userId, rackCode) = await SeedRackAsync(db, InventoryStorageMode.FloorStaging);
        var service = new LocationSlotService(db, new OperationLogGuard(db));

        var result = await service.ConfirmLocationSlotsAsync(Guid.NewGuid(), rackCode, [new SlotDefinition("P01", "Position 1", 1)], userId);

        var created = await db.InventoryLocations.Where(x => x.ParentInventoryLocationId == result.ParentInventoryLocationId).ToListAsync();
        Assert.All(created, x => Assert.Equal(InventoryLocationType.Staging, x.LocationType));
    }

    [Fact]
    public async Task ConfirmLocationSlotsAsync_NonRackParent_Throws()
    {
        await using var db = _fixture.CreateContext();
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
        var area = new InventoryLocation
        {
            LocationCode = $"AREA-{Guid.NewGuid():N}"[..20], DisplayName = "Area", LocationType = InventoryLocationType.Area,
            StorageMode = InventoryStorageMode.Other, SortOrder = 0, IsActive = true
        };
        db.AddRange(user, area);
        await db.SaveChangesAsync();
        var service = new LocationSlotService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmLocationSlotsAsync(Guid.NewGuid(), area.LocationCode, [new SlotDefinition("S01", "Shelf 1", 1)], user.AppUserId));
    }

    [Fact]
    public async Task ConfirmLocationSlotsAsync_DuplicateSlotCode_ThrowsConflict()
    {
        await using var db = _fixture.CreateContext();
        var (userId, rackCode) = await SeedRackAsync(db, InventoryStorageMode.RolloutShelf);
        var service = new LocationSlotService(db, new OperationLogGuard(db));
        await service.ConfirmLocationSlotsAsync(Guid.NewGuid(), rackCode, [new SlotDefinition("S01", "Shelf 1", 1)], userId);

        await Assert.ThrowsAsync<LocationSlotConflictException>(() =>
            service.ConfirmLocationSlotsAsync(Guid.NewGuid(), rackCode, [new SlotDefinition("S01", "Shelf 1 Again", 1)], userId));
    }

    [Fact]
    public async Task ConfirmLocationSlotsAsync_RetryWithSameCorrelationId_ReturnsReplayWithoutDuplicating()
    {
        await using var db = _fixture.CreateContext();
        var (userId, rackCode) = await SeedRackAsync(db, InventoryStorageMode.RolloutShelf);
        var service = new LocationSlotService(db, new OperationLogGuard(db));
        var correlationId = Guid.NewGuid();
        var slots = new[] { new SlotDefinition("S01", "Shelf 1", 1) };

        var first = await service.ConfirmLocationSlotsAsync(correlationId, rackCode, slots, userId);
        var second = await service.ConfirmLocationSlotsAsync(correlationId, rackCode, slots, userId);

        Assert.Equal(first.CreatedLocationCodes, second.CreatedLocationCodes);
        var count = await db.InventoryLocations.CountAsync(x => x.ParentInventoryLocationId == first.ParentInventoryLocationId);
        Assert.Equal(1, count);
    }
}
