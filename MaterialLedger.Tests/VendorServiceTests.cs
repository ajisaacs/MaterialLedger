using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Vendors;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class VendorServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public VendorServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private static async Task<int> SeedUserAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser
        {
            Username = $"vendor_test_{Guid.NewGuid():N}",
            PasswordHash = "unused",
            DisplayName = "Vendor Test User",
            Role = AppUserRole.Manager,
            IsActive = true
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user.AppUserId;
    }

    [Fact]
    public async Task CreateVendorAsync_RejectsBlankAndOverlengthNames()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new VendorService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateVendorAsync(
            Guid.NewGuid(), new CreateVendorRequest("   ", null, null, null, null), userId));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateVendorAsync(
            Guid.NewGuid(), new CreateVendorRequest(new string('x', 201), null, null, null, null), userId));
    }

    [Fact]
    public async Task CreateVendorAndPartAsync_TrimValuesAndRejectNormalizedDuplicates()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new VendorService(db, new OperationLogGuard(db));
        var vendorName = $"Acme Metals {Guid.NewGuid():N}";

        var vendor = await service.CreateVendorAsync(
            Guid.NewGuid(), new CreateVendorRequest($"  {vendorName}  ", null, null, null, null), userId);
        Assert.Equal(vendorName, vendor.Name);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateVendorAsync(
            Guid.NewGuid(), new CreateVendorRequest(vendorName.ToLowerInvariant(), null, null, null, null), userId));

        var part = await service.CreateVendorPartAsync(
            Guid.NewGuid(), new CreateVendorPartRequest(vendor.VendorId, "  A-100  ", null, null, null), userId);
        Assert.Equal("A-100", part.PartNumber);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateVendorPartAsync(
            Guid.NewGuid(), new CreateVendorPartRequest(vendor.VendorId, "a-100", null, null, null), userId));
    }

    [Fact]
    public async Task UpdateVendorAsync_DeactivatesInsteadOfDeleting()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new VendorService(db, new OperationLogGuard(db));
        var vendorName = $"Acme Metals {Guid.NewGuid():N}";
        var vendor = await service.CreateVendorAsync(
            Guid.NewGuid(), new CreateVendorRequest(vendorName, null, null, null, null), userId);

        var updated = await service.UpdateVendorAsync(vendor.VendorId,
            new UpdateVendorRequest(vendorName, null, null, null, null, false), userId);

        Assert.False(updated.IsActive);
        Assert.Equal(1, await db.Vendors.CountAsync(x => x.VendorId == vendor.VendorId));
    }

    [Fact]
    public async Task CreateVendorAsync_ReplayReturnsRecordedVendorWithoutDuplicate()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new VendorService(db, new OperationLogGuard(db));
        var correlationId = Guid.NewGuid();
        var request = new CreateVendorRequest($"Replay vendor {Guid.NewGuid():N}", null, null, null, null);

        var first = await service.CreateVendorAsync(correlationId, request, userId);
        var replay = await service.CreateVendorAsync(correlationId, request, userId);

        Assert.Equal(first.VendorId, replay.VendorId);
        Assert.Equal(1, await db.Vendors.CountAsync(x => x.VendorId == first.VendorId));
    }

    [Fact]
    public async Task CreateVendorAsync_ReplayByAnotherUserIsRejected()
    {
        await using var db = _fixture.CreateContext();
        var ownerId = await SeedUserAsync(db);
        var otherUserId = await SeedUserAsync(db);
        var service = new VendorService(db, new OperationLogGuard(db));
        var correlationId = Guid.NewGuid();
        var request = new CreateVendorRequest($"Owned vendor {Guid.NewGuid():N}", null, null, null, null);

        await service.CreateVendorAsync(correlationId, request, ownerId);

        await Assert.ThrowsAsync<OperationOwnershipException>(() =>
            service.CreateVendorAsync(correlationId, request, otherUserId));
    }

    [Fact]
    public async Task CreateVendorPartAsync_ReplayReturnsRecordedPartWithItsVendorOwnership()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new VendorService(db, new OperationLogGuard(db));
        var vendor = await service.CreateVendorAsync(Guid.NewGuid(),
            new CreateVendorRequest($"Part owner {Guid.NewGuid():N}", null, null, null, null), userId);
        var correlationId = Guid.NewGuid();
        var request = new CreateVendorPartRequest(vendor.VendorId, "Owned part", null, null, null);

        var first = await service.CreateVendorPartAsync(correlationId, request, userId);
        var replay = await service.CreateVendorPartAsync(correlationId, request, userId);

        Assert.Equal(first.VendorPartId, replay.VendorPartId);
        Assert.Equal(vendor.VendorId, replay.VendorId);
        Assert.Equal(1, await db.VendorParts.CountAsync(x => x.VendorPartId == first.VendorPartId));
    }

    [Fact]
    public async Task FindOrCreateActiveVendorAsync_ConcurrentSameKeyReturnsOneVendor()
    {
        await using var setupDb = _fixture.CreateContext();
        var userId = await SeedUserAsync(setupDb);
        var vendorName = $"Concurrent vendor {Guid.NewGuid():N}";

        await using var firstDb = _fixture.CreateContext();
        await using var secondDb = _fixture.CreateContext();
        var firstService = new VendorService(firstDb, new OperationLogGuard(firstDb));
        var secondService = new VendorService(secondDb, new OperationLogGuard(secondDb));

        var created = await Task.WhenAll(
            firstService.FindOrCreateActiveVendorAsync(vendorName, userId),
            secondService.FindOrCreateActiveVendorAsync($"  {vendorName.ToLowerInvariant()}  ", userId));

        Assert.Equal(created[0].VendorId, created[1].VendorId);
        await using var verificationDb = _fixture.CreateContext();
        Assert.Equal(1, await verificationDb.Vendors.CountAsync(x => x.NormalizedName == vendorName.ToUpperInvariant()));
    }
}
