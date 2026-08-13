using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class OperationLogGuardTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public OperationLogGuardTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<int> SeedUserAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser
        {
            Username = $"test_{Guid.NewGuid():N}",
            PasswordHash = "unused",
            DisplayName = "Test User",
            Role = AppUserRole.Programmer,
            IsActive = true
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user.AppUserId;
    }

    [Fact]
    public async Task CheckAsync_UnseenCorrelationId_ReturnsUnseen()
    {
        await using var db = _fixture.CreateContext();
        var guard = new OperationLogGuard(db);

        var outcome = await guard.CheckAsync(Guid.NewGuid(), "TestProcedure", "hash-a", executedBy: 1);

        Assert.Equal(OperationLogOutcomeKind.Unseen, outcome.Kind);
    }

    [Fact]
    public async Task CheckAsync_SeenCorrelationIdMatchingHash_ReturnsReplayWithStoredResult()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var guard = new OperationLogGuard(db);
        var correlationId = Guid.NewGuid();

        await guard.RecordAsync(correlationId, "TestProcedure", "hash-a", "original-result", userId);
        var outcome = await guard.CheckAsync(correlationId, "TestProcedure", "hash-a", userId);

        Assert.Equal(OperationLogOutcomeKind.Replay, outcome.Kind);
        Assert.Equal("original-result", outcome.ResultSummary);
    }

    [Fact]
    public async Task CheckAsync_SeenCorrelationIdMismatchedHash_ThrowsConflict()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var guard = new OperationLogGuard(db);
        var correlationId = Guid.NewGuid();

        await guard.RecordAsync(correlationId, "TestProcedure", "hash-a", "original-result", userId);

        await Assert.ThrowsAsync<OperationConflictException>(
            () => guard.CheckAsync(correlationId, "TestProcedure", "hash-b", userId));
    }

    [Fact]
    public async Task CheckAsync_SeenCorrelationIdMatchingHashDifferentProcedure_ThrowsConflict()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var guard = new OperationLogGuard(db);
        var correlationId = Guid.NewGuid();

        await guard.RecordAsync(correlationId, "FirstProcedure", "hash-a", "original-result", userId);

        await Assert.ThrowsAsync<OperationConflictException>(
            () => guard.CheckAsync(correlationId, "SecondProcedure", "hash-a", userId));
    }

    [Fact]
    public async Task CheckAsync_SeenCorrelationIdMatchingRequestOwnedByDifferentUser_ThrowsWithoutReturningResult()
    {
        await using var db = _fixture.CreateContext();
        var ownerId = await SeedUserAsync(db);
        var otherUserId = await SeedUserAsync(db);
        var guard = new OperationLogGuard(db);
        var correlationId = Guid.NewGuid();

        await guard.RecordAsync(correlationId, "TestProcedure", "hash-a", "secret-result", ownerId);

        await Assert.ThrowsAsync<OperationOwnershipException>(
            () => guard.CheckAsync(correlationId, "TestProcedure", "hash-a", otherUserId));
    }
}
