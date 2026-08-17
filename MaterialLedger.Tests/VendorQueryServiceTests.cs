using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Vendors;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class VendorQueryServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public VendorQueryServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PriceHistoryAsync_ExcludesCostlessReceiptsOrdersActualsAndRetainsAllQuoteStatuses()
    {
        await using var db = _fixture.CreateContext();
        var (part, olderReceipt, newerReceipt, costlessReceipt, datedOrder, undatedOrder) = await SeedPriceHistoryAsync(db);
        var service = new VendorQueryService(db);

        var actuals = await service.GetActualPriceHistoryAsync(part.VendorPartId);
        var quotes = await service.GetQuotedPriceHistoryAsync(part.VendorPartId);
        var summary = await service.GetPriceSummaryAsync(part.VendorPartId);

        Assert.Equal([newerReceipt.SheetLotId, olderReceipt.SheetLotId], actuals.Select(x => x.SheetLotId));
        Assert.DoesNotContain(actuals, x => x.SheetLotId == costlessReceipt.SheetLotId);
        Assert.Equal(20m, actuals[0].CostPerUnit);
        Assert.Equal(datedOrder.PurchaseOrderNumber, actuals[0].PurchaseOrderNumber);
        Assert.Equal([datedOrder.PurchaseOrderId, undatedOrder.PurchaseOrderId], quotes.Select(x => x.PurchaseOrderId));
        Assert.Equal(PurchaseOrderLineStatus.Cancelled, quotes.Single(x => x.PurchaseOrderId == undatedOrder.PurchaseOrderId).Status);
        Assert.Null(quotes.Single(x => x.PurchaseOrderId == undatedOrder.PurchaseOrderId).OrderDate);
        Assert.Equal(20m, summary.LastActual);
        Assert.Equal(newerReceipt.ReceivedAtUtc, summary.LastActualAt);
        Assert.Equal(15m, summary.AvgActual);
        Assert.Equal(2, summary.ActualPoints);
        Assert.Equal(2, summary.QuotedPoints);
    }

    [Fact]
    public async Task GetVendorPartAsync_ReturnsInactivePartAndEmptyHistoryHasNullPriceSummary()
    {
        await using var db = _fixture.CreateContext();
        var vendor = new Vendor { Name = $"Vendor {Guid.NewGuid():N}", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        var part = new VendorPart { Vendor = vendor, PartNumber = "Inactive part", IsActive = false, CreatedAtUtc = DateTime.UtcNow };
        db.VendorParts.Add(part);
        await db.SaveChangesAsync();
        var service = new VendorQueryService(db);

        var result = await service.GetVendorPartAsync(part.VendorPartId);
        var summary = await service.GetPriceSummaryAsync(part.VendorPartId);

        Assert.Equal(part.VendorPartId, result.VendorPartId);
        Assert.False(result.IsActive);
        Assert.Equal(vendor.VendorId, result.VendorId);
        Assert.Null(summary.LastActual);
        Assert.Null(summary.LastActualAt);
        Assert.Null(summary.AvgActual);
        Assert.Equal(0, summary.ActualPoints);
        Assert.Equal(0, summary.QuotedPoints);
    }

    [Fact]
    public async Task GetVendorsAsync_OrdersByNameAndIncludesInactiveVendors()
    {
        await using var db = _fixture.CreateContext();
        var suffix = Guid.NewGuid().ToString("N");
        db.Vendors.AddRange(
            new Vendor { Name = $"Zulu {suffix}", IsActive = false, CreatedAtUtc = DateTime.UtcNow },
            new Vendor { Name = $"Alpha {suffix}", IsActive = true, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var service = new VendorQueryService(db);

        var vendors = await service.GetVendorsAsync();
        var matching = vendors.Where(x => x.Name.EndsWith(suffix, StringComparison.Ordinal)).ToList();

        Assert.Equal([$"Alpha {suffix}", $"Zulu {suffix}"], matching.Select(x => x.Name));
        Assert.Contains(matching, x => !x.IsActive);
    }

    [Fact]
    public async Task GetVendorPartAsync_ThrowsForMissingPart()
    {
        await using var db = _fixture.CreateContext();
        var service = new VendorQueryService(db);

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetVendorPartAsync(long.MaxValue));

        Assert.Contains("Vendor part", exception.Message);
    }

    private static async Task<(VendorPart part, SheetLot olderReceipt, SheetLot newerReceipt, SheetLot costlessReceipt, PurchaseOrder datedOrder, PurchaseOrder undatedOrder)> SeedPriceHistoryAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"vendor_query_{Guid.NewGuid():N}", PasswordHash = "unused", DisplayName = "Vendor Query User", Role = AppUserRole.Manager, IsActive = true };
        var spec = new MaterialSpec { MaterialFamily = "Steel", Grade = $"A36-{Guid.NewGuid():N}"[..30], ThicknessIn = 0.25m, DisplayName = "A36 quarter inch", IsActive = true };
        var vendor = new Vendor { Name = $"Vendor {Guid.NewGuid():N}", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        var part = new VendorPart { Vendor = vendor, PartNumber = "VP-100", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.AddRange(user, spec, part);
        await db.SaveChangesAsync();

        var datedOrder = new PurchaseOrder { VendorId = vendor.VendorId, PurchaseOrderNumber = $"PO-{Guid.NewGuid():N}", OrderDate = new DateOnly(2026, 8, 10), CreatedAtUtc = DateTime.UtcNow, CreatedBy = user.AppUserId };
        var undatedOrder = new PurchaseOrder { VendorId = vendor.VendorId, PurchaseOrderNumber = $"PO-{Guid.NewGuid():N}", CreatedAtUtc = DateTime.UtcNow, CreatedBy = user.AppUserId };
        db.PurchaseOrders.AddRange(datedOrder, undatedOrder);
        await db.SaveChangesAsync();
        db.PurchaseOrderLines.AddRange(
            new PurchaseOrderLine { PurchaseOrderId = datedOrder.PurchaseOrderId, VendorPartId = part.VendorPartId, LineNumber = 1, MaterialSpecId = spec.MaterialSpecId, QuantityOrdered = 4m, UnitPrice = 21m, Status = PurchaseOrderLineStatus.Open },
            new PurchaseOrderLine { PurchaseOrderId = undatedOrder.PurchaseOrderId, VendorPartId = part.VendorPartId, LineNumber = 1, MaterialSpecId = spec.MaterialSpecId, QuantityOrdered = 3m, UnitPrice = 22m, Status = PurchaseOrderLineStatus.Cancelled });
        var olderReceipt = new SheetLot { MaterialSpecId = spec.MaterialSpecId, VendorId = vendor.VendorId, VendorPartId = part.VendorPartId, VendorName = vendor.Name, VendorPartNumber = part.PartNumber, PurchaseOrderNumber = datedOrder.PurchaseOrderNumber, ReceiptNumber = "R-OLD", SourceType = SheetLotSourceType.Receipt, ReceivedAtUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), CostPerUnit = 10m };
        var newerReceipt = new SheetLot { MaterialSpecId = spec.MaterialSpecId, VendorId = vendor.VendorId, VendorPartId = part.VendorPartId, VendorName = vendor.Name, VendorPartNumber = part.PartNumber, PurchaseOrderNumber = datedOrder.PurchaseOrderNumber, ReceiptNumber = "R-NEW", SourceType = SheetLotSourceType.Receipt, ReceivedAtUtc = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc), CostPerUnit = 20m };
        var costlessReceipt = new SheetLot { MaterialSpecId = spec.MaterialSpecId, VendorId = vendor.VendorId, VendorPartId = part.VendorPartId, VendorName = vendor.Name, VendorPartNumber = part.PartNumber, SourceType = SheetLotSourceType.Receipt, ReceivedAtUtc = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc) };
        db.AddRange(olderReceipt, newerReceipt, costlessReceipt);
        await db.SaveChangesAsync();
        return (part, olderReceipt, newerReceipt, costlessReceipt, datedOrder, undatedOrder);
    }
}
