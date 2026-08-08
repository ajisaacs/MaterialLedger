using MaterialLedger.Data;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class InventoryQueryServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public InventoryQueryServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long rackId, long slotId, long specId)> SeedAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Programmer, IsActive = true, PasswordHash = "x" };
        var rack = new InventoryLocation { LocationCode = $"RACK-{Guid.NewGuid():N}"[..20], DisplayName = "Rack", LocationType = InventoryLocationType.Rack, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        db.AddRange(user, rack);
        await db.SaveChangesAsync();
        var slot = new InventoryLocation { LocationCode = $"{rack.LocationCode}:S01", DisplayName = "Shelf 1", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, ParentInventoryLocationId = rack.InventoryLocationId, IsActive = true };
        var guid = Guid.NewGuid().ToString("N");
        var spec = new MaterialSpec { MaterialFamily = $"Steel-{guid}"[..30], Grade = $"A36-{guid}"[..30], ThicknessIn = 0.25m, DisplayName = "A36 1/4in", IsActive = true };
        db.AddRange(slot, spec);
        await db.SaveChangesAsync();
        return (user.AppUserId, rack.InventoryLocationId, slot.InventoryLocationId, spec.MaterialSpecId);
    }

    private static InventoryItem NewBundle(long slotId, long specId, int observedBy, decimal qty) => new()
    {
        PublicItemCode = $"SHEET-{Guid.NewGuid():N}"[..20],
        MaterialSpecId = specId,
        InventoryLocationId = slotId,
        ItemKind = InventoryItemKind.FullSheetBundle,
        Status = InventoryItemStatus.Available,
        QuantityOnHand = qty,
        ShapeClassification = ShapeClassification.Rectangular,
        LastObservedAtUtc = DateTime.UtcNow,
        LastObservedBy = observedBy
    };

    [Fact]
    public async Task SearchAsync_ComputesAvailableQuantityMinusActiveReservations()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _, slotId, specId) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 6m);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        db.MaterialReservations.Add(new MaterialReservation { InventoryItemId = item.InventoryItemId, Status = ReservationStatus.Approved, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedBy = userId });
        db.MaterialReservations.Add(new MaterialReservation { InventoryItemId = item.InventoryItemId, Status = ReservationStatus.Proposed, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedBy = userId });
        db.MaterialReservations.Add(new MaterialReservation { InventoryItemId = item.InventoryItemId, Status = ReservationStatus.Released, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedBy = userId });
        await db.SaveChangesAsync();
        var slot = await db.InventoryLocations.FindAsync(slotId);
        var service = new InventoryQueryService(db, freshnessThresholdDays: 90);

        var results = await service.SearchAsync(new InventoryFilter(null, null, null, null, null, null, null, slot!.LocationCode, false));

        var summary = Assert.Single(results);
        Assert.Equal(6m, summary.QuantityOnHand);
        Assert.Equal(4m, summary.AvailableQuantity); // 6 - (1 Approved + 1 Proposed); Released excluded
    }

    [Fact]
    public async Task SearchAsync_FiltersByMaterialFamilyAndMinWidth()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _, slotId, specId) = await SeedAsync(db);
        db.InventoryItems.Add(NewBundle(slotId, specId, userId, 3m));
        var guid = Guid.NewGuid().ToString("N");
        var otherSpec = new MaterialSpec { MaterialFamily = $"Aluminum-{guid}"[..30], Grade = $"5052-{guid}"[..30], ThicknessIn = 0.25m, DisplayName = "5052 1/4in", IsActive = true };
        db.MaterialSpecs.Add(otherSpec);
        await db.SaveChangesAsync();
        var drop = new InventoryItem
        {
            PublicItemCode = $"DROP-{Guid.NewGuid():N}"[..20], MaterialSpecId = otherSpec.MaterialSpecId, InventoryLocationId = slotId,
            ItemKind = InventoryItemKind.Drop, Status = InventoryItemStatus.Available, QuantityOnHand = 1,
            UsableWidthIn = 12m, UsableLengthIn = 24m, ShapeClassification = ShapeClassification.Rectangular,
            LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = userId
        };
        db.InventoryItems.Add(drop);
        await db.SaveChangesAsync();
        var service = new InventoryQueryService(db, freshnessThresholdDays: 90);

        var results = await service.SearchAsync(new InventoryFilter($"Aluminum-{guid}"[..30], null, null, null, null, 10m, null, null, false));

        var summary = Assert.Single(results);
        Assert.Equal(drop.InventoryItemId, summary.InventoryItemId);
    }

    [Fact]
    public async Task SearchAsync_FlagsStaleObservation()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _, slotId, specId) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 2m);
        item.LastObservedAtUtc = DateTime.UtcNow.AddDays(-91);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var slot = await db.InventoryLocations.FindAsync(slotId);
        var service = new InventoryQueryService(db, freshnessThresholdDays: 90);

        var results = await service.SearchAsync(new InventoryFilter(null, null, null, null, null, null, null, slot!.LocationCode, false));

        Assert.True(Assert.Single(results).IsStale);
    }

    [Fact]
    public async Task SearchAsync_AvailableOnly_ExcludesQuarantinedStaleAndFullyReserved()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _, slotId, specId) = await SeedAsync(db);
        var quarantined = NewBundle(slotId, specId, userId, 2m);
        quarantined.Status = InventoryItemStatus.Quarantined;
        var stale = NewBundle(slotId, specId, userId, 2m);
        stale.LastObservedAtUtc = DateTime.UtcNow.AddDays(-91);
        var fullyReserved = NewBundle(slotId, specId, userId, 1m);
        var available = NewBundle(slotId, specId, userId, 3m);
        db.InventoryItems.AddRange(quarantined, stale, fullyReserved, available);
        await db.SaveChangesAsync();
        db.MaterialReservations.Add(new MaterialReservation { InventoryItemId = fullyReserved.InventoryItemId, Status = ReservationStatus.Approved, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedBy = userId });
        await db.SaveChangesAsync();
        var slot = await db.InventoryLocations.FindAsync(slotId);
        var service = new InventoryQueryService(db, freshnessThresholdDays: 90);

        var results = await service.SearchAsync(new InventoryFilter(null, null, null, null, null, null, null, slot!.LocationCode, true));

        var summary = Assert.Single(results);
        Assert.Equal(available.InventoryItemId, summary.InventoryItemId);
    }

    [Fact]
    public async Task GetRackSummariesAsync_CountsAvailableReservedAndExceptionsPerRack()
    {
        await using var db = _fixture.CreateContext();
        var (userId, rackId, slotId, specId) = await SeedAsync(db);
        var available = NewBundle(slotId, specId, userId, 5m);
        var quarantined = NewBundle(slotId, specId, userId, 1m);
        quarantined.Status = InventoryItemStatus.Quarantined;
        var reservedDrop = new InventoryItem
        {
            PublicItemCode = $"DROP-{Guid.NewGuid():N}"[..20], MaterialSpecId = specId, InventoryLocationId = slotId,
            ItemKind = InventoryItemKind.Drop, Status = InventoryItemStatus.Reserved, QuantityOnHand = 1,
            UsableWidthIn = 10m, UsableLengthIn = 10m, ShapeClassification = ShapeClassification.Rectangular,
            LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = userId
        };
        db.InventoryItems.AddRange(available, quarantined, reservedDrop);
        await db.SaveChangesAsync();
        var service = new InventoryQueryService(db, freshnessThresholdDays: 90);
        var rackCode = (await db.InventoryLocations.FindAsync(rackId))!.LocationCode;

        var summaries = await service.GetRackSummariesAsync();

        var rackSummary = summaries.Single(x => x.LocationCode == rackCode);
        Assert.Equal(1, rackSummary.AvailableItemCount);
        Assert.Equal(1, rackSummary.ReservedItemCount);
        Assert.Equal(1, rackSummary.ExceptionItemCount);
    }

    [Fact]
    public async Task GetItemsAtLocationsAsync_ReturnsOnlyItemsAtGivenLocations()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _, slotId, specId) = await SeedAsync(db);
        var inScope = NewBundle(slotId, specId, userId, 2m);
        var otherSlot = new InventoryLocation { LocationCode = $"OTHER-{Guid.NewGuid():N}"[..20], DisplayName = "Other", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        db.InventoryLocations.Add(otherSlot);
        await db.SaveChangesAsync();
        var outOfScope = NewBundle(otherSlot.InventoryLocationId, specId, userId, 2m);
        db.InventoryItems.AddRange(inScope, outOfScope);
        await db.SaveChangesAsync();
        var service = new InventoryQueryService(db, freshnessThresholdDays: 90);

        var results = await service.GetItemsAtLocationsAsync([slotId]);

        var summary = Assert.Single(results);
        Assert.Equal(inScope.InventoryItemId, summary.InventoryItemId);
    }
}
