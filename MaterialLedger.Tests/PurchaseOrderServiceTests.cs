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

    private static CreatePurchaseOrderRequest MakeRequest(long specId, decimal quantity = 2m, string poNumber = "46318") =>
        new("O'Neal Steel", poNumber, new DateOnly(2026, 8, 6), "All HR sheets on pallet",
            [new PurchaseOrderLineRequest(specId, "3/16 X48 X8", "796598", quantity, 48m, 96m, 213.36m, new DateOnly(2026, 8, 7))]);

    [Fact]
    public async Task CreatePurchaseOrderAsync_CreatesOrderAndLines_Atomically()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        var orderId = await service.CreatePurchaseOrderAsync(Guid.NewGuid(), MakeRequest(specId), userId);

        var order = await db.PurchaseOrders.Include(x => x.Lines).SingleAsync(x => x.PurchaseOrderId == orderId);
        Assert.Equal("O'Neal Steel", order.VendorName);
        Assert.Equal("46318", order.PurchaseOrderNumber);
        Assert.Single(order.Lines);
        Assert.Equal(1, order.Lines[0].LineNumber);
        Assert.Equal(PurchaseOrderLineStatus.Open, order.Lines[0].Status);
        Assert.Equal(0m, order.Lines[0].QuantityReceived);
    }

    [Fact]
    public async Task CreatePurchaseOrderAsync_RejectsBlankVendorName()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId) = await SeedAsync(db);
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));
        var request = MakeRequest(specId) with { VendorName = "   " };

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePurchaseOrderAsync(Guid.NewGuid(), request, userId));
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
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));
        var request = MakeRequest(specId);
        var correlationId = Guid.NewGuid();

        var first = await service.CreatePurchaseOrderAsync(correlationId, request, userId);
        var second = await service.CreatePurchaseOrderAsync(correlationId, request, userId);

        Assert.Equal(first, second);
        var count = await db.PurchaseOrders.CountAsync(x => x.PurchaseOrderNumber == "46318");
        Assert.Equal(1, count);
    }
}
