using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.PurchaseOrders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class PurchaseOrderServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public PurchaseOrderServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long specId)> SeedAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Programmer, IsActive = true, PasswordHash = "x" };
        var guid = Guid.NewGuid().ToString("N");
        var spec = new MaterialSpec { MaterialFamily = $"Plate-{guid}"[..30], Grade = "A36", ThicknessIn = 0.1875m, DisplayName = "A36 3/16in", IsActive = true };
        db.AddRange(user, spec);
        await db.SaveChangesAsync();
        return (user.AppUserId, spec.MaterialSpecId);
    }

    private static CreatePurchaseOrderRequest MakeRequest(long specId, long vendorId = 0, long? vendorPartId = null,
        decimal quantity = 2m, string poNumber = "46318") =>
        new(vendorId, poNumber, new DateOnly(2026, 8, 6), "All HR sheets on pallet",
            [new PurchaseOrderLineRequest(specId, vendorPartId, "3/16 X48 X8", quantity, 48m, 96m, 213.36m, new DateOnly(2026, 8, 7))]);

    private static async Task<(long vendorId, long vendorPartId)> SeedVendorAsync(MaterialLedgerDbContext db, bool vendorActive = true, bool partActive = true)
    {
        var vendor = new Vendor { Name = $"Vendor-{Guid.NewGuid():N}", IsActive = vendorActive, CreatedAtUtc = DateTime.UtcNow };
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();
        var part = new VendorPart { VendorId = vendor.VendorId, PartNumber = $"Part-{Guid.NewGuid():N}", IsActive = partActive, CreatedAtUtc = DateTime.UtcNow };
        db.VendorParts.Add(part);
        await db.SaveChangesAsync();
        return (vendor.VendorId, part.VendorPartId);
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_CreatesOrderAndLines_Atomically()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var (vendorId, vendorPartId) = await SeedVendorAsync(db);
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        var orderId = await service.CreatePurchaseOrderAsync(Guid.NewGuid(), MakeRequest(specId, vendorId, vendorPartId), userId);

        var order = await db.PurchaseOrders.Include(x => x.Lines).SingleAsync(x => x.PurchaseOrderId == orderId);
        Assert.Equal(vendorId, order.VendorId);
        Assert.Equal("46318", order.PurchaseOrderNumber);
        Assert.Single(order.Lines);
        Assert.Equal(1, order.Lines[0].LineNumber);
        Assert.Equal(PurchaseOrderLineStatus.Open, order.Lines[0].Status);
        Assert.Equal(0m, order.Lines[0].QuantityReceived);
        Assert.Equal(vendorPartId, order.Lines[0].VendorPartId);
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_RejectsInactiveVendor()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var (vendorId, _) = await SeedVendorAsync(db, vendorActive: false);
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));
        var request = MakeRequest(specId, vendorId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreatePurchaseOrderAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_RejectsNoLines()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _) = await SeedAsync(db);
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));
        var request = MakeRequest(0) with { Lines = [] };

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePurchaseOrderAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_RejectsFractionalLineQuantity()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));
        var request = MakeRequest(specId, quantity: 2.5m);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePurchaseOrderAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_RetryWithSameCorrelationId_DoesNotDuplicate()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var (vendorId, _) = await SeedVendorAsync(db);
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));
        var request = MakeRequest(specId, vendorId);
        var correlationId = Guid.NewGuid();

        var first = await service.CreatePurchaseOrderAsync(correlationId, request, userId);
        var second = await service.CreatePurchaseOrderAsync(correlationId, request, userId);

        Assert.Equal(first, second);
        var count = await db.PurchaseOrders.CountAsync(x => x.PurchaseOrderNumber == "46318");
        Assert.Equal(1, count);
    }

    private async Task<long> SeedLineAsync(MaterialLedgerDbContext db, int userId, long specId, decimal quantityOrdered, decimal quantityReceived = 0)
    {
        var (vendorId, _) = await SeedVendorAsync(db);
        var order = new PurchaseOrder
        {
            VendorId = vendorId, PurchaseOrderNumber = $"PO-{Guid.NewGuid():N}"[..20],
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = userId
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        var status = quantityReceived == 0 ? PurchaseOrderLineStatus.Open
            : quantityReceived >= quantityOrdered ? PurchaseOrderLineStatus.Received
            : PurchaseOrderLineStatus.PartiallyReceived;
        var line = new PurchaseOrderLine
        {
            PurchaseOrderId = order.PurchaseOrderId, LineNumber = 1, MaterialSpecId = specId,
            QuantityOrdered = quantityOrdered, QuantityReceived = quantityReceived, Status = status
        };
        db.PurchaseOrderLines.Add(line);
        await db.SaveChangesAsync();
        return line.PurchaseOrderLineId;
    }

    [Fact]
    public async Task CancelLineAsync_OpenLine_SetsCancelled()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var lineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 5m);
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        await service.CancelLineAsync(Guid.NewGuid(), lineId, userId);

        var line = await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == lineId);
        Assert.Equal(PurchaseOrderLineStatus.Cancelled, line.Status);
    }

    [Fact]
    public async Task CancelLineAsync_AlreadyReceivedLine_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var lineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 5m, quantityReceived: 5m);
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelLineAsync(Guid.NewGuid(), lineId, userId));
    }

    [Fact]
    public async Task CloseRemainingAsync_CancelsOnlyNonReceivedLines()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var openLineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 5m);
        var partialLineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 5m, quantityReceived: 2m);
        var receivedLineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 5m, quantityReceived: 5m);
        var openLine = await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == openLineId);
        var purchaseOrderId = openLine.PurchaseOrderId;
        // move the other two lines onto the same order so CloseRemaining sees all three
        var partialLine = await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == partialLineId);
        partialLine.PurchaseOrderId = purchaseOrderId;
        partialLine.LineNumber = 2;
        var receivedLine = await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == receivedLineId);
        receivedLine.PurchaseOrderId = purchaseOrderId;
        receivedLine.LineNumber = 3;
        await db.SaveChangesAsync();
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        var cancelledCount = await service.CloseRemainingAsync(Guid.NewGuid(), purchaseOrderId, userId);

        Assert.Equal(2, cancelledCount);
        Assert.Equal(PurchaseOrderLineStatus.Cancelled, (await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == openLineId)).Status);
        Assert.Equal(PurchaseOrderLineStatus.Cancelled, (await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == partialLineId)).Status);
        Assert.Equal(PurchaseOrderLineStatus.Received, (await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == receivedLineId)).Status);
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_RejectsOverLengthNotes()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));
        var request = MakeRequest(specId) with { Notes = new string('x', 1001) };

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePurchaseOrderAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_RetryWithSameCorrelationId_DifferentPayload_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var (vendorId, _) = await SeedVendorAsync(db);
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));
        var correlationId = Guid.NewGuid();
        await service.CreatePurchaseOrderAsync(correlationId, MakeRequest(specId, vendorId, poNumber: "46318"), userId);

        await Assert.ThrowsAsync<MaterialLedger.Data.Idempotency.OperationConflictException>(
            () => service.CreatePurchaseOrderAsync(correlationId, MakeRequest(specId, vendorId, poNumber: "DIFFERENT-PO"), userId));
    }

    private static InventoryLocation MakeLocation(InventoryLocationType type = InventoryLocationType.Shelf) => new()
    {
        LocationCode = $"LOC-{Guid.NewGuid():N}",
        DisplayName = "Test location",
        LocationType = type,
        StorageMode = InventoryStorageMode.RolloutShelf,
        IsActive = true
    };

    [Fact]
    public async Task AddLocationAllocationAsync_CreatesAllocation_VisibleViaLineNavigation()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var lineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 28m);
        var location = MakeLocation();
        db.InventoryLocations.Add(location);
        await db.SaveChangesAsync();
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        var allocationId = await service.AddLocationAllocationAsync(
            Guid.NewGuid(), new AddLocationAllocationRequest(lineId, location.InventoryLocationId, 20m), userId);

        var line = await db.PurchaseOrderLines.Include(x => x.LocationAllocations).SingleAsync(x => x.PurchaseOrderLineId == lineId);
        Assert.Single(line.LocationAllocations);
        Assert.Equal(allocationId, line.LocationAllocations[0].PurchaseOrderLineAllocationId);
        Assert.Equal(20m, line.LocationAllocations[0].QuantityPlanned);
        Assert.Equal(location.InventoryLocationId, line.LocationAllocations[0].InventoryLocationId);
    }

    [Fact]
    public async Task AddLocationAllocationAsync_RejectsNonLeafLocation()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var lineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 28m);
        var rack = MakeLocation(InventoryLocationType.Rack);
        db.InventoryLocations.Add(rack);
        await db.SaveChangesAsync();
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddLocationAllocationAsync(Guid.NewGuid(), new AddLocationAllocationRequest(lineId, rack.InventoryLocationId, 5m), userId));
    }

    [Fact]
    public async Task AddLocationAllocationAsync_RejectsQuantityLessThanOne()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var lineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 28m);
        var location = MakeLocation();
        db.InventoryLocations.Add(location);
        await db.SaveChangesAsync();
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddLocationAllocationAsync(Guid.NewGuid(), new AddLocationAllocationRequest(lineId, location.InventoryLocationId, 0m), userId));
    }

    [Fact]
    public async Task AddLocationAllocationAsync_RejectsFractionalQuantity()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var lineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 28m);
        var location = MakeLocation();
        db.InventoryLocations.Add(location);
        await db.SaveChangesAsync();
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddLocationAllocationAsync(Guid.NewGuid(), new AddLocationAllocationRequest(lineId, location.InventoryLocationId, 2.5m), userId));
    }

    [Fact]
    public async Task AddLocationAllocationAsync_RejectsWhenExceedsLineRemaining()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var lineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 10m, quantityReceived: 2m);
        var location = MakeLocation();
        db.InventoryLocations.Add(location);
        await db.SaveChangesAsync();
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddLocationAllocationAsync(Guid.NewGuid(), new AddLocationAllocationRequest(lineId, location.InventoryLocationId, 9m), userId));
    }

    [Fact]
    public async Task AddLocationAllocationAsync_RejectsWhenCombinedWithExistingExceedsRemaining()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var lineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 28m);
        var locationA = MakeLocation();
        var locationB = MakeLocation();
        db.InventoryLocations.AddRange(locationA, locationB);
        await db.SaveChangesAsync();
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));
        await service.AddLocationAllocationAsync(Guid.NewGuid(), new AddLocationAllocationRequest(lineId, locationA.InventoryLocationId, 20m), userId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddLocationAllocationAsync(Guid.NewGuid(), new AddLocationAllocationRequest(lineId, locationB.InventoryLocationId, 9m), userId));
    }

    [Fact]
    public async Task AddLocationAllocationAsync_RejectsReceivedLine()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var lineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 10m);
        var line = await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == lineId);
        line.Status = PurchaseOrderLineStatus.Received;
        await db.SaveChangesAsync();
        var location = MakeLocation();
        db.InventoryLocations.Add(location);
        await db.SaveChangesAsync();
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddLocationAllocationAsync(Guid.NewGuid(), new AddLocationAllocationRequest(lineId, location.InventoryLocationId, 1m), userId));
    }

    [Fact]
    public async Task AddLocationAllocationAsync_RejectsCancelledLine()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var lineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 10m);
        var line = await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == lineId);
        line.Status = PurchaseOrderLineStatus.Cancelled;
        await db.SaveChangesAsync();
        var location = MakeLocation();
        db.InventoryLocations.Add(location);
        await db.SaveChangesAsync();
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddLocationAllocationAsync(Guid.NewGuid(), new AddLocationAllocationRequest(lineId, location.InventoryLocationId, 1m), userId));
    }

    [Fact]
    public async Task AddLocationAllocationAsync_RetryWithSameCorrelationId_DoesNotDuplicate()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var lineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 28m);
        var location = MakeLocation();
        db.InventoryLocations.Add(location);
        await db.SaveChangesAsync();
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));
        var correlationId = Guid.NewGuid();
        var request = new AddLocationAllocationRequest(lineId, location.InventoryLocationId, 20m);

        var first = await service.AddLocationAllocationAsync(correlationId, request, userId);
        var second = await service.AddLocationAllocationAsync(correlationId, request, userId);

        Assert.Equal(first, second);
        var count = await db.PurchaseOrderLineAllocations.CountAsync(x => x.PurchaseOrderLineId == lineId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RemoveLocationAllocationAsync_DeletesRow()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var lineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 28m);
        var location = MakeLocation();
        db.InventoryLocations.Add(location);
        await db.SaveChangesAsync();
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));
        var allocationId = await service.AddLocationAllocationAsync(
            Guid.NewGuid(), new AddLocationAllocationRequest(lineId, location.InventoryLocationId, 20m), userId);

        await service.RemoveLocationAllocationAsync(Guid.NewGuid(), allocationId, userId);

        Assert.False(await db.PurchaseOrderLineAllocations.AnyAsync(x => x.PurchaseOrderLineAllocationId == allocationId));
    }

    [Fact]
    public async Task RemoveLocationAllocationAsync_NoOpOnMissingId()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _) = await SeedAsync(db);
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        await service.RemoveLocationAllocationAsync(Guid.NewGuid(), 999_999_999L, userId);

        Assert.False(await db.PurchaseOrderLineAllocations.AnyAsync(x => x.PurchaseOrderLineAllocationId == 999_999_999L));
    }

    [Fact]
    public async Task RemoveLocationAllocationAsync_RetryWithSameCorrelationId_DoesNotDoubleDelete()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var lineId = await SeedLineAsync(db, userId, specId, quantityOrdered: 28m);
        var location = MakeLocation();
        db.InventoryLocations.Add(location);
        await db.SaveChangesAsync();
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));
        var allocationId = await service.AddLocationAllocationAsync(
            Guid.NewGuid(), new AddLocationAllocationRequest(lineId, location.InventoryLocationId, 20m), userId);
        var correlationId = Guid.NewGuid();

        await service.RemoveLocationAllocationAsync(correlationId, allocationId, userId);
        await service.RemoveLocationAllocationAsync(correlationId, allocationId, userId);

        Assert.False(await db.PurchaseOrderLineAllocations.AnyAsync(x => x.PurchaseOrderLineAllocationId == allocationId));
    }
}
