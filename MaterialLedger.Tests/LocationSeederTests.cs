using MaterialLedger.Data.Models;
using MaterialLedger.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class LocationSeederTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public LocationSeederTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SeedAsync_CreatesThirteenLocations_AreaPlusTenRacksPlusTwoSkids()
    {
        await using var db = _fixture.CreateContext();
        var seeder = new LocationSeeder(db);

        await seeder.SeedAsync();

        var count = await db.InventoryLocations.CountAsync();
        Assert.Equal(13, count);

        var area = await db.InventoryLocations.SingleAsync(x => x.LocationCode == "LASER-MATERIAL");
        Assert.Equal(InventoryLocationType.Area, area.LocationType);
        Assert.Null(area.ParentInventoryLocationId);

        var southRoll = await db.InventoryLocations.SingleAsync(x => x.LocationCode == "L1-SOUTH-ROLL");
        Assert.Equal(area.InventoryLocationId, southRoll.ParentInventoryLocationId);
        Assert.Equal(InventoryLocationType.Rack, southRoll.LocationType);
        Assert.Equal(InventoryStorageMode.RolloutShelf, southRoll.StorageMode);
        Assert.False(southRoll.CapacityConfirmed);

        var eastDrop = await db.InventoryLocations.SingleAsync(x => x.LocationCode == "EAST-DROP");
        Assert.Equal(InventoryStorageMode.VerticalDropBay, eastDrop.StorageMode);

        var skid = await db.InventoryLocations.SingleAsync(x => x.LocationCode == "SKID-EAST-WEST");
        Assert.Equal(InventoryStorageMode.FloorStaging, skid.StorageMode);
        Assert.Equal(area.InventoryLocationId, skid.ParentInventoryLocationId);
    }

    [Fact]
    public async Task SeedAsync_uses_cutting_wide_location_names_and_updates_the_prior_laser_branded_seed_values()
    {
        await using var db = _fixture.CreateContext();
        var seeder = new LocationSeeder(db);

        await seeder.SeedAsync();
        var area = await db.InventoryLocations.SingleAsync(x => x.LocationCode == "LASER-MATERIAL");
        var southRoll = await db.InventoryLocations.SingleAsync(x => x.LocationCode == "L1-SOUTH-ROLL");
        var northRoll = await db.InventoryLocations.SingleAsync(x => x.LocationCode == "L1-NORTH-ROLL");
        var secondRoll = await db.InventoryLocations.SingleAsync(x => x.LocationCode == "L2-ROLL");
        area.DisplayName = "Laser Material Storage Area";
        southRoll.DisplayName = "Laser 1 South Roll-Out Rack";
        northRoll.DisplayName = "Laser 1 North Roll-Out Rack";
        secondRoll.DisplayName = "Laser 2 Roll-Out Rack";
        await db.SaveChangesAsync();

        await seeder.SeedAsync();

        Assert.Equal("Material Storage Area", area.DisplayName);
        Assert.Equal("South Roll-Out Rack", southRoll.DisplayName);
        Assert.Equal("North Roll-Out Rack", northRoll.DisplayName);
        Assert.Equal("Roll-Out Rack 2", secondRoll.DisplayName);
    }

    [Fact]
    public async Task SeedAsync_RunTwice_StaysIdempotent()
    {
        await using var db = _fixture.CreateContext();
        var seeder = new LocationSeeder(db);

        await seeder.SeedAsync();
        await seeder.SeedAsync();

        var count = await db.InventoryLocations.CountAsync();
        Assert.Equal(13, count);
    }
}
