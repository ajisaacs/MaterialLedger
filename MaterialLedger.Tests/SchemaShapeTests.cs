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
    public async Task AllEighteenMaterialTables_Exist()
    {
        await using var db = _fixture.CreateContext();
        await using var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'material'";
        var count = (int)(await command.ExecuteScalarAsync())!;
        Assert.Equal(18, count);
    }

    [Fact]
    public async Task VendorsAndVendorParts_HavePersistedComputedKeysAndUniqueIndexes()
    {
        await using var db = _fixture.CreateContext();
        await using var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.computed_columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'material'
              AND ((t.name = 'Vendors' AND c.name = 'NormalizedName' AND c.is_computed = 1 AND c.is_persisted = 1)
                OR (t.name = 'VendorParts' AND c.name = 'NormalizedPartNumber' AND c.is_computed = 1 AND c.is_persisted = 1));

            SELECT COUNT(*)
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'material' AND i.is_unique = 1
              AND ((t.name = 'Vendors' AND i.name = 'IX_Vendors_NormalizedName')
                OR (t.name = 'VendorParts' AND i.name = 'IX_VendorParts_VendorId_NormalizedPartNumber'));
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
    }

    [Fact]
    public async Task VendorParts_RejectWhitespaceAndCaseEquivalentPartNumbers()
    {
        await using var db = _fixture.CreateContext();
        await using var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var insertVendor = connection.CreateCommand();
        insertVendor.CommandText = "INSERT INTO material.Vendors (Name, IsActive, CreatedAtUtc) OUTPUT INSERTED.VendorId VALUES (N'Test Vendor', 1, SYSUTCDATETIME());";
        var vendorId = (long)(await insertVendor.ExecuteScalarAsync())!;

        await using var insertPart = connection.CreateCommand();
        insertPart.CommandText = "INSERT INTO material.VendorParts (VendorId, PartNumber, IsActive, CreatedAtUtc) VALUES (@vendorId, N'Part-100', 1, SYSUTCDATETIME());";
        insertPart.Parameters.AddWithValue("@vendorId", vendorId);
        await insertPart.ExecuteNonQueryAsync();

        await using var duplicatePart = connection.CreateCommand();
        duplicatePart.CommandText = "INSERT INTO material.VendorParts (VendorId, PartNumber, IsActive, CreatedAtUtc) VALUES (@vendorId, N'  part-100  ', 1, SYSUTCDATETIME());";
        duplicatePart.Parameters.AddWithValue("@vendorId", vendorId);
        await Assert.ThrowsAsync<SqlException>(() => duplicatePart.ExecuteNonQueryAsync());

        await using var blankPart = connection.CreateCommand();
        blankPart.CommandText = "INSERT INTO material.VendorParts (VendorId, PartNumber, IsActive, CreatedAtUtc) VALUES (@vendorId, N'   ', 1, SYSUTCDATETIME());";
        blankPart.Parameters.AddWithValue("@vendorId", vendorId);
        await Assert.ThrowsAsync<SqlException>(() => blankPart.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Vendors_RejectWhitespaceOnlyNames()
    {
        await using var db = _fixture.CreateContext();
        await using var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO material.Vendors (Name, IsActive, CreatedAtUtc) VALUES (N'   ', 1, SYSUTCDATETIME());";

        await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task VendorReferences_AreFinalizedAndSheetLotSnapshotsRemain()
    {
        await using var db = _fixture.CreateContext();
        await using var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'material' AND
                ((t.name = 'PurchaseOrders' AND c.name = 'VendorId' AND c.is_nullable = 0) OR
                 (t.name = 'PurchaseOrderLines' AND c.name = 'VendorPartId' AND c.is_nullable = 1) OR
                 (t.name = 'SheetLots' AND c.name = 'VendorId' AND c.is_nullable = 1) OR
                 (t.name = 'SheetLots' AND c.name = 'VendorPartId' AND c.is_nullable = 1) OR
                 (t.name = 'SheetLots' AND c.name = 'VendorName') OR
                 (t.name = 'SheetLots' AND c.name = 'VendorPartNumber'));

            SELECT COUNT(*)
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'material' AND
                ((t.name = 'PurchaseOrders' AND c.name = 'VendorName') OR
                 (t.name = 'PurchaseOrderLines' AND c.name = 'VendorPartNumber'));

            SELECT COUNT(*)
            FROM sys.foreign_keys fk
            JOIN sys.tables parent ON parent.object_id = fk.parent_object_id
            JOIN sys.schemas s ON s.schema_id = parent.schema_id
            WHERE s.name = 'material' AND fk.name IN
                ('FK_PurchaseOrders_Vendors_VendorId',
                 'FK_PurchaseOrderLines_VendorParts_VendorPartId',
                 'FK_SheetLots_Vendors_VendorId',
                 'FK_SheetLots_VendorParts_VendorPartId')
              AND fk.delete_referential_action = 0;

            SELECT COUNT(*)
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'material' AND i.name IN
                ('IX_PurchaseOrders_VendorId',
                 'IX_PurchaseOrderLines_VendorPartId',
                 'IX_SheetLots_VendorId',
                 'IX_SheetLots_VendorPartId');
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(6, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(4, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(4, reader.GetInt32(0));
    }

    [Fact]
    public async Task OpenNestReferences_AreAddedBesidePepColumns()
    {
        await using var db = _fixture.CreateContext();
        await using var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE s.name = 'material' AND t.name = 'MaterialReservations' AND c.is_nullable = 1 AND
                ((c.name = 'OpenNestNestId' AND ty.name = 'uniqueidentifier') OR
                 (c.name = 'NestName' AND ty.name = 'nvarchar' AND c.max_length = 400) OR
                 (c.name = 'PepNestYear') OR
                 (c.name = 'PepNestName'));

            SELECT COUNT(*)
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'material' AND t.name = 'MaterialReservations' AND i.name = 'IX_MaterialReservations_OpenNestNestId';

            SELECT cc.definition
            FROM sys.check_constraints cc
            JOIN sys.tables t ON t.object_id = cc.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'material' AND cc.name = 'CK_MaterialSpecExternalMapping_ExternalSystem';
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(4, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        var definition = reader.GetString(0);
        Assert.Contains("N'PEP'", definition);
        Assert.Contains("N'OpenNest'", definition);
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

    [Fact]
    public async Task PurchaseOrderLine_RejectsFractionalQuantityReceived()
    {
        await using var db = _fixture.CreateContext();
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
        var spec = new MaterialSpec { MaterialFamily = "Carbon Steel", Grade = $"A36-{Guid.NewGuid():N}", ThicknessIn = 0.25m, DisplayName = "A36 1/4in", IsActive = true };
        var vendor = new Vendor { Name = $"Test Vendor {Guid.NewGuid():N}", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.AddRange(user, spec, vendor);
        await db.SaveChangesAsync();

        var po = new PurchaseOrder
        {
            VendorId = vendor.VendorId,
            PurchaseOrderNumber = $"PO-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = user.AppUserId
        };
        db.PurchaseOrders.Add(po);
        await db.SaveChangesAsync();

        db.PurchaseOrderLines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = po.PurchaseOrderId,
            LineNumber = 1,
            MaterialSpecId = spec.MaterialSpecId,
            QuantityOrdered = 3m,
            QuantityReceived = 1.5m, // invalid: quantity received must be a whole number
            Status = PurchaseOrderLineStatus.PartiallyReceived
        });

        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => db.SaveChangesAsync());
    }
}
