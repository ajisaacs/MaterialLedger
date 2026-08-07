using MaterialLedger.Data.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

// SqlServerTestDatabaseFixture provisions a single physical database named
// "MaterialLedgerTest" on the shared forge-sql server. IClassFixture<T> gives each
// test class its own fixture instance, and xunit runs test classes in separate
// collections in parallel by default. With two test classes now consuming this
// fixture (OperationLogGuardTests from Task 6, SchemaShapeTests here), parallel
// execution causes both fixture instances to race to create/drop the same database
// name, producing spurious "Database already exists" failures unrelated to schema
// correctness. Disable cross-collection parallelization for this assembly so the
// fixtures run sequentially instead.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class SchemaShapeTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public SchemaShapeTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AllTwelveMaterialTables_Exist()
    {
        await using var db = _fixture.CreateContext();
        await using var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'material'";
        var count = (int)(await command.ExecuteScalarAsync())!;
        Assert.Equal(12, count);
    }

    [Fact]
    public async Task ItemCodeSequence_Exists()
    {
        await using var db = _fixture.CreateContext();
        await using var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.sequences WHERE name = 'ItemCodeSeq'";
        var count = (int)(await command.ExecuteScalarAsync())!;
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task InventoryItem_RejectsFractionalQuantityForFullSheet()
    {
        await using var db = _fixture.CreateContext();
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
        var spec = new MaterialSpec { MaterialFamily = "Carbon Steel", Grade = "A36", ThicknessIn = 0.25m, DisplayName = "A36 1/4in", IsActive = true };
        var location = new InventoryLocation { LocationCode = $"LOC-{Guid.NewGuid():N}", DisplayName = "Test Shelf", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        db.AddRange(user, spec, location);
        await db.SaveChangesAsync();

        db.InventoryItems.Add(new InventoryItem
        {
            PublicItemCode = $"TEST-{Guid.NewGuid():N}",
            MaterialSpecId = spec.MaterialSpecId,
            InventoryLocationId = location.InventoryLocationId,
            ItemKind = InventoryItemKind.FullSheet,
            Status = InventoryItemStatus.Available,
            QuantityOnHand = 1.5m, // invalid: full sheets must be whole numbers
            ShapeClassification = ShapeClassification.Rectangular,
            LastObservedAtUtc = DateTime.UtcNow,
            LastObservedBy = user.AppUserId
        });

        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => db.SaveChangesAsync());
    }
}
