using MaterialLedger.Data;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class NestingStockQueryServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public NestingStockQueryServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private sealed record Seeded(int UserId, string RackCode, long SlotId, string Family);

    private static async Task<Seeded> SeedAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Programmer, IsActive = true, PasswordHash = "x" };
        var rack = new InventoryLocation { LocationCode = $"RACK-{Guid.NewGuid():N}"[..20], DisplayName = "Rack", LocationType = InventoryLocationType.Rack, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        db.AddRange(user, rack);
        await db.SaveChangesAsync();
        var slot = new InventoryLocation { LocationCode = $"{rack.LocationCode}:S01", DisplayName = "Shelf 1", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, ParentInventoryLocationId = rack.InventoryLocationId, IsActive = true };
        db.Add(slot);
        await db.SaveChangesAsync();
        return new Seeded(user.AppUserId, rack.LocationCode, slot.InventoryLocationId, $"Fam-{Guid.NewGuid():N}"[..30]);
    }

    private static async Task<MaterialSpec> SpecAsync(MaterialLedgerDbContext db, string family, string? grade, string? finish, decimal thickness)
    {
        var spec = new MaterialSpec { MaterialFamily = family, Grade = grade, Finish = finish, ThicknessIn = thickness, DisplayName = $"{family} {grade} {finish} {thickness}", IsActive = true };
        db.Add(spec);
        await db.SaveChangesAsync();
        return spec;
    }

    private static InventoryItem Item(Seeded s, MaterialSpec spec, InventoryItemKind kind, decimal qty, decimal? width, decimal? length,
        InventoryItemStatus status = InventoryItemStatus.Available, DateTime? observed = null) => new()
    {
        PublicItemCode = $"IT-{Guid.NewGuid():N}"[..20],
        MaterialSpecId = spec.MaterialSpecId,
        InventoryLocationId = s.SlotId,
        ItemKind = kind,
        Status = status,
        QuantityOnHand = qty,
        UsableWidthIn = width,
        UsableLengthIn = length,
        ShapeClassification = ShapeClassification.Rectangular,
        LastObservedAtUtc = observed ?? DateTime.UtcNow,
        LastObservedBy = s.UserId,
    };

    [Fact]
    public async Task GetAsync_ReturnsOnlyFreeSizedStockOfTheExactMaterial()
    {
        await using var db = _fixture.CreateContext();
        var s = await SeedAsync(db);
        var hr = await SpecAsync(db, s.Family, "A1011", "HR", 0.1345m);
        var hrpo = await SpecAsync(db, s.Family, "A1011", "HRPO", 0.1345m);
        var thicker = await SpecAsync(db, s.Family, "A1011", "HR", 0.25m);

        var bundle = Item(s, hr, InventoryItemKind.FullSheetBundle, 3m, 60m, 120m);
        var drop = Item(s, hr, InventoryItemKind.Drop, 1m, 40m, 22m); // entered long side first
        var held = Item(s, hr, InventoryItemKind.Drop, 1m, 20m, 30m);
        var unsized = Item(s, hr, InventoryItemKind.FullSheet, 1m, null, null);
        var stale = Item(s, hr, InventoryItemKind.FullSheet, 1m, 48m, 96m, observed: DateTime.UtcNow.AddDays(-200));
        var quarantined = Item(s, hr, InventoryItemKind.FullSheet, 1m, 48m, 96m, InventoryItemStatus.Quarantined);
        var otherFinish = Item(s, hrpo, InventoryItemKind.FullSheet, 1m, 48m, 96m);
        var otherThickness = Item(s, thicker, InventoryItemKind.FullSheet, 1m, 48m, 96m);
        db.InventoryItems.AddRange(bundle, drop, held, unsized, stale, quarantined, otherFinish, otherThickness);
        await db.SaveChangesAsync();
        db.MaterialReservations.AddRange(
            new MaterialReservation { InventoryItemId = bundle.InventoryItemId, Status = ReservationStatus.Approved, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedBy = s.UserId },
            new MaterialReservation { InventoryItemId = held.InventoryItemId, Status = ReservationStatus.Proposed, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedBy = s.UserId });
        await db.SaveChangesAsync();
        var service = new NestingStockQueryService(db, freshnessThresholdDays: 90);

        // Lower case and 0.0005" off still match: SQL Server compares case-insensitively, thickness within 0.001".
        var result = await service.GetAsync(new NestingStockQuery(s.Family.ToLowerInvariant(), "a1011", "hr", 0.135m));

        Assert.Equal(hr.MaterialSpecId, Assert.Single(result.Specs).MaterialSpecId);
        var items = result.Items;
        Assert.Equal(
            new[] { bundle.PublicItemCode, drop.PublicItemCode }.Order(StringComparer.Ordinal),
            items.Select(i => i.ItemCode).Order(StringComparer.Ordinal));
        var bundleRow = items.Single(i => i.ItemCode == bundle.PublicItemCode);
        Assert.Equal((2, 60m, 120m), (bundleRow.AvailableQuantity, bundleRow.WidthIn, bundleRow.LengthIn));
        var dropRow = items.Single(i => i.ItemCode == drop.PublicItemCode);
        Assert.Equal((InventoryItemKind.Drop, 22m, 40m, 1), (dropRow.Kind, dropRow.WidthIn, dropRow.LengthIn, dropRow.AvailableQuantity));
        Assert.Equal(s.RackCode, dropRow.ParentLocationCode);
    }

    [Fact]
    public async Task GetAsync_BlankFinishMatchesOnlyMaterialsWithoutAFinish()
    {
        await using var db = _fixture.CreateContext();
        var s = await SeedAsync(db);
        var plain = await SpecAsync(db, s.Family, "A36", null, 0.25m);
        await SpecAsync(db, s.Family, "A36", "PO", 0.25m);

        var result = await new NestingStockQueryService(db, 90).GetAsync(new NestingStockQuery(s.Family, "A36", "", 0.25m));

        Assert.Equal(plain.MaterialSpecId, Assert.Single(result.Specs).MaterialSpecId);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetAsync_UnknownMaterialReturnsNoSpecs()
    {
        await using var db = _fixture.CreateContext();
        var result = await new NestingStockQueryService(db, 90).GetAsync(new NestingStockQuery($"Nope-{Guid.NewGuid():N}"[..30], "X", null, 0.25m));
        Assert.Empty(result.Specs);
        Assert.Empty(result.Items);
    }
}
