using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Census;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Pages;
using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class QuickSheetEntryIntegrationTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public QuickSheetEntryIntegrationTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long specId, long slotId)> SeedAsync(MaterialLedgerDbContext db)
    {
        var guid = Guid.NewGuid().ToString("N");
        var user = new AppUser { Username = $"t_{guid}"[..30], DisplayName = "T", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
        var spec = new MaterialSpec { MaterialFamily = $"Plate-{guid}"[..30], Grade = "A36", ThicknessIn = 0.1875m, DisplayName = "A36 3/16in", IsActive = true };
        db.AddRange(user, spec);
        await db.SaveChangesAsync();

        var rack = new InventoryLocation
        {
            LocationCode = $"RACK-{guid}"[..30], DisplayName = "East drop rack", LocationType = InventoryLocationType.Rack,
            StorageMode = InventoryStorageMode.VerticalDropBay, IsActive = true, CapacityConfirmed = true
        };
        db.InventoryLocations.Add(rack);
        await db.SaveChangesAsync();

        var slot = new InventoryLocation
        {
            LocationCode = $"SLOT-{guid}"[..30], DisplayName = "East drop rack — A1", LocationType = InventoryLocationType.Shelf,
            StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true, CapacityConfirmed = true,
            ParentInventoryLocationId = rack.InventoryLocationId
        };
        db.InventoryLocations.Add(slot);
        await db.SaveChangesAsync();

        return (user.AppUserId, spec.MaterialSpecId, slot.InventoryLocationId);
    }

    private static IndexModel NewModel(MaterialLedgerDbContext db, int userId) => new(
        new WorkQueueQueryService(db, new InventoryQueryService(db, freshnessThresholdDays: 90)),
        db,
        new OpeningBalanceService(db, new OperationLogGuard(db)))
    {
        PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, "Manager")], "test"))
            }
        }
    };

    [Fact]
    public async Task OnPostAddSheetAsync_GoldenPath_ReturnsSuccessJsonAndCreatesItem()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId, slotId) = await SeedAsync(db);
        var model = NewModel(db, userId);
        model.NewSheet = new NewSheetInput { LocationId = slotId, ExistingMaterialSpecId = specId, UsableWidthIn = 48m, UsableLengthIn = 96m };

        var result = await model.OnPostAddSheetAsync();

        var json = Assert.IsType<JsonResult>(result);
        var response = Assert.IsType<AddSheetResponse>(json.Value);
        Assert.True(response.Success);
        Assert.Null(response.Error);
        Assert.NotNull(response.ItemCode);
        Assert.StartsWith("SHEET-", response.ItemCode);
        Assert.Equal("A36 3/16in", response.MaterialDisplayName);
        Assert.Equal(48m, response.UsableWidthIn);
        Assert.Equal(96m, response.UsableLengthIn);
        Assert.Equal(1, await db.InventoryItems.CountAsync(x => x.PublicItemCode == response.ItemCode));
    }

    [Fact]
    public async Task OnPostAddSheetAsync_InvalidSlot_ReturnsErrorJsonAndCreatesNothing()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId, _) = await SeedAsync(db);
        var model = NewModel(db, userId);
        var countBefore = await db.InventoryItems.CountAsync();
        model.NewSheet = new NewSheetInput { LocationId = -1, ExistingMaterialSpecId = specId };

        var result = await model.OnPostAddSheetAsync();

        var json = Assert.IsType<JsonResult>(result);
        var response = Assert.IsType<AddSheetResponse>(json.Value);
        Assert.False(response.Success);
        Assert.Equal("Choose an active material slot.", response.Error);
        Assert.Equal(countBefore, await db.InventoryItems.CountAsync());
    }

    [Fact]
    public async Task OnPostAddSheetAsync_TwoConsecutiveAdds_BothSucceed()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId, slotId) = await SeedAsync(db);
        var model = NewModel(db, userId);
        model.NewSheet = new NewSheetInput { LocationId = slotId, ExistingMaterialSpecId = specId, UsableWidthIn = 48m, UsableLengthIn = 96m };
        var first = Assert.IsType<AddSheetResponse>(Assert.IsType<JsonResult>(await model.OnPostAddSheetAsync()).Value);

        model.NewSheet = new NewSheetInput { LocationId = slotId, ExistingMaterialSpecId = specId, UsableWidthIn = 30m, UsableLengthIn = 60m };
        var second = Assert.IsType<AddSheetResponse>(Assert.IsType<JsonResult>(await model.OnPostAddSheetAsync()).Value);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.NotEqual(first.ItemCode, second.ItemCode);
        Assert.Equal(2, await db.InventoryItems.CountAsync());
    }
}
