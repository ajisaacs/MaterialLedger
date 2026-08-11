using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.PurchaseOrders;
using MaterialLedger.Data.Receiving;
using MaterialLedger.Web.Pages.PurchaseOrders;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class ReceiveAllIntegrationTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public ReceiveAllIntegrationTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long specId)> SeedAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Programmer, IsActive = true, PasswordHash = "x" };
        var guid = Guid.NewGuid().ToString("N");
        var spec = new MaterialSpec { MaterialFamily = $"Plate-{guid}"[..30], Grade = "A36", ThicknessIn = 0.1875m, DisplayName = "A36 3/16in", IsActive = true };
        db.AddRange(user, spec);
        await db.SaveChangesAsync();
        return (user.AppUserId, spec.MaterialSpecId);
    }

    private static InventoryLocation MakeLocation() => new()
    {
        LocationCode = $"LOC-{Guid.NewGuid():N}"[..30],
        DisplayName = "Test location",
        LocationType = InventoryLocationType.Shelf,
        StorageMode = InventoryStorageMode.RolloutShelf,
        IsActive = true
    };

    private async Task<long> SeedOrderAsync(MaterialLedgerDbContext db, int userId)
    {
        var order = new PurchaseOrder
        {
            VendorName = "Acme Steel",
            PurchaseOrderNumber = $"PO-{Guid.NewGuid():N}"[..20],
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = userId
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order.PurchaseOrderId;
    }

    private static ReceiveAllModel NewModel(MaterialLedgerDbContext db, ReceiveMaterialService receiveMaterial, PurchaseOrderService purchaseOrderService, int userId) =>
        new(db, receiveMaterial, purchaseOrderService) { PageContext = NewPageContext(userId) };

    private static PageContext NewPageContext(int userId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        return new PageContext { HttpContext = context };
    }

    [Fact]
    public async Task OnPostAsync_GoldenPath_ReceivesAllocationRowAndRemainderRow_AndClearsAllocation()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var purchaseOrderService = new PurchaseOrderService(db, new OperationLogGuard(db));
        var receiveMaterial = new ReceiveMaterialService(db, new OperationLogGuard(db));

        var orderId = await SeedOrderAsync(db, userId);
        var location = MakeLocation();
        db.InventoryLocations.Add(location);
        await db.SaveChangesAsync();
        var line = new PurchaseOrderLine
        {
            PurchaseOrderId = orderId, LineNumber = 1, MaterialSpecId = specId,
            QuantityOrdered = 20m, QuantityReceived = 0m, UnitPrice = 5m, Status = PurchaseOrderLineStatus.Open
        };
        db.PurchaseOrderLines.Add(line);
        await db.SaveChangesAsync();

        var allocationId = await purchaseOrderService.AddLocationAllocationAsync(
            Guid.NewGuid(), new AddLocationAllocationRequest(line.PurchaseOrderLineId, location.InventoryLocationId, 12m), userId);

        var model = NewModel(db, receiveMaterial, purchaseOrderService, userId);
        model.Id = orderId;
        await model.OnGetAsync();
        Assert.Equal(2, model.Rows.Count);

        // Select both the allocation-backed row and the remainder row; give the remainder row a location.
        foreach (var row in model.Rows) row.Selected = true;
        var remainderRow = model.Rows.Single(r => r.AllocationId is null);
        remainderRow.LocationId = location.InventoryLocationId;

        await model.OnPostAsync();

        Assert.All(model.Rows, row =>
        {
            Assert.Null(row.Error);
            Assert.NotNull(row.SucceededItemCode);
        });

        var allocationRow = model.Rows.Single(r => r.AllocationId == allocationId);
        Assert.False(await db.PurchaseOrderLineAllocations.AnyAsync(x => x.PurchaseOrderLineAllocationId == allocationId));

        var refreshedLine = await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == line.PurchaseOrderLineId);
        Assert.Equal(20m, refreshedLine.QuantityReceived);
        Assert.Equal(PurchaseOrderLineStatus.Received, refreshedLine.Status);
    }

    [Fact]
    public async Task OnPostAsync_ResubmitWithSameCorrelationIds_DoesNotDuplicateReceipts()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var purchaseOrderService = new PurchaseOrderService(db, new OperationLogGuard(db));
        var receiveMaterial = new ReceiveMaterialService(db, new OperationLogGuard(db));

        var orderId = await SeedOrderAsync(db, userId);
        var location = MakeLocation();
        db.InventoryLocations.Add(location);
        await db.SaveChangesAsync();
        var line = new PurchaseOrderLine
        {
            PurchaseOrderId = orderId, LineNumber = 1, MaterialSpecId = specId,
            QuantityOrdered = 10m, QuantityReceived = 0m, UnitPrice = 5m, Status = PurchaseOrderLineStatus.Open
        };
        db.PurchaseOrderLines.Add(line);
        await db.SaveChangesAsync();

        var model = NewModel(db, receiveMaterial, purchaseOrderService, userId);
        model.Id = orderId;
        await model.OnGetAsync();
        var row = Assert.Single(model.Rows);
        row.Selected = true;
        row.LocationId = location.InventoryLocationId;

        await model.OnPostAsync();
        var firstItemCode = model.Rows[0].SucceededItemCode;
        Assert.NotNull(firstItemCode);

        // Resubmit with the same row object (same CorrelationId, unchanged) — simulates a duplicate POST.
        await model.OnPostAsync();

        Assert.Null(model.Rows[0].Error);
        Assert.Equal(firstItemCode, model.Rows[0].SucceededItemCode);

        var itemCount = await db.InventoryItems.CountAsync(x => x.InventoryLocationId == location.InventoryLocationId);
        Assert.Equal(1, itemCount);
        var lotCount = await db.SheetLots.CountAsync(x => x.MaterialSpecId == specId);
        Assert.Equal(1, lotCount);
    }

    [Fact]
    public async Task OnPostAsync_PartialFailure_IsolatesBadRowFromGoodRow()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var purchaseOrderService = new PurchaseOrderService(db, new OperationLogGuard(db));
        var receiveMaterial = new ReceiveMaterialService(db, new OperationLogGuard(db));

        var orderId = await SeedOrderAsync(db, userId);
        var locationA = MakeLocation();
        var locationB = MakeLocation();
        db.InventoryLocations.AddRange(locationA, locationB);
        await db.SaveChangesAsync();
        var lineA = new PurchaseOrderLine
        {
            PurchaseOrderId = orderId, LineNumber = 1, MaterialSpecId = specId,
            QuantityOrdered = 10m, QuantityReceived = 0m, UnitPrice = 5m, Status = PurchaseOrderLineStatus.Open
        };
        var lineB = new PurchaseOrderLine
        {
            PurchaseOrderId = orderId, LineNumber = 2, MaterialSpecId = specId,
            QuantityOrdered = 5m, QuantityReceived = 0m, UnitPrice = 5m, Status = PurchaseOrderLineStatus.Open
        };
        db.PurchaseOrderLines.AddRange(lineA, lineB);
        await db.SaveChangesAsync();

        var model = NewModel(db, receiveMaterial, purchaseOrderService, userId);
        model.Id = orderId;
        await model.OnGetAsync();
        Assert.Equal(2, model.Rows.Count);

        var goodRow = model.Rows.Single(r => r.PurchaseOrderLineId == lineA.PurchaseOrderLineId);
        goodRow.Selected = true;
        goodRow.LocationId = locationA.InventoryLocationId;

        var badRow = model.Rows.Single(r => r.PurchaseOrderLineId == lineB.PurchaseOrderLineId);
        badRow.Selected = true;
        badRow.LocationId = locationB.InventoryLocationId;
        badRow.Quantity = 999m; // exceeds the line's remaining quantity of 5

        await model.OnPostAsync();

        Assert.NotNull(goodRow.SucceededItemCode);
        Assert.Null(goodRow.Error);

        Assert.Null(badRow.SucceededItemCode);
        Assert.NotNull(badRow.Error);

        var refreshedLineA = await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == lineA.PurchaseOrderLineId);
        Assert.Equal(10m, refreshedLineA.QuantityReceived);
        var refreshedLineB = await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == lineB.PurchaseOrderLineId);
        Assert.Equal(0m, refreshedLineB.QuantityReceived);
    }
}
