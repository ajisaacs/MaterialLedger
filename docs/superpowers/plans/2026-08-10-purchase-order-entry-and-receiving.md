# Purchase Order Entry & Receiving-Against-PO Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a Programmer/Manager record a purchase order (vendor, PO#, line items) when it's placed, track what's outstanding, and receive material against a specific PO line instead of only free-form receiving.

**Architecture:** Two new EF entities (`PurchaseOrder`, `PurchaseOrderLine`) plus a nullable `SheetLot.PurchaseOrderLineId` link. A new `PurchaseOrderService` (create/cancel/close, following the existing `OpeningBalanceService`/`ReceiveMaterialService` transactional-idempotency shape) and a small extension to the existing `ReceiveMaterialService` to optionally consume against a line. Three new Razor Pages (`PurchaseOrders/Index|Create|Details`) plus a `poLineId` query-string hookup added to the existing `Receiving/SelectLocation` → `Receiving/NewReceipt` flow.

**Tech Stack:** ASP.NET Core Razor Pages (.NET 8), EF Core 9 / SQL Server, xUnit integration tests against a real throwaway SQL Server database.

## Global Constraints

- Every new write path (create PO, cancel line, close remaining, receive-against-line) follows the exact transactional/idempotency shape used throughout this codebase: SHA-256 payload hash → `BeginTransactionAsync(IsolationLevel.Serializable)` → `OperationLogGuard.CheckAsync`/replay → business writes → `OperationLogGuard.Record` (not `RecordAsync`) → final `SaveChangesAsync()` → `CommitAsync()`.
- New/modified pages are gated `[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]` — the same role gate as `Receiving`/`Census`.
- Forms use `method="post"` + a single raw `@Html.AntiForgeryToken()` and **never** mix `asp-page`/`asp-route-*` tag helpers with an explicit antiforgery token on the same `<form>` (standing codebase rule, hit multiple times before). Plain `<a>` navigation links may freely use `asp-page`/`asp-route-*`.
- Decimal columns follow existing precision conventions: quantities `decimal(12,3)`, prices `decimal(12,4)`, geometry `decimal(10,3)`. Enums are stored via `.HasConversion<string>().HasMaxLength(20)` (or the existing max length for that enum family).
- New schema/permission files are additive only: next EF migration, and `docs/schema/material-permissions-v8.sql` — **never edit v1–v7**. Permission files are applied manually against the real Forge SQL Server by a human; nothing in this plan applies them automatically.
- No PDF parsing/OCR — line items are typed in by hand from the vendor confirmation.
- Match established file-organization convention: a header entity and its owned child entity live in one model file (e.g. `MaterialReservation`+`ExpectedRemnant` today) — so `PurchaseOrder` and `PurchaseOrderLine` share one file.

---

### Task 1: Data model, EF migration, permissions grant

**Files:**
- Create: `MaterialLedger.Data/Models/PurchaseOrder.cs`
- Modify: `MaterialLedger.Data/Models/SheetLot.cs`
- Modify: `MaterialLedger.Data/Models/Attachment.cs`
- Modify: `MaterialLedger.Data/MaterialLedgerDbContext.cs`
- Create: EF migration (generated file under `MaterialLedger.Data/Migrations/`)
- Create: `docs/schema/material-permissions-v8.sql`
- Modify: `MaterialLedger.Tests/SchemaShapeTests.cs`

**Interfaces:**
- Produces: `PurchaseOrder`, `PurchaseOrderLine`, `PurchaseOrderLineStatus` (enum: `Open`, `PartiallyReceived`, `Received`, `Cancelled`) in `MaterialLedger.Data.Models`. `MaterialLedgerDbContext.PurchaseOrders` / `.PurchaseOrderLines` (`DbSet<T>`). `SheetLot.PurchaseOrderLineId` (`long?`) / `.PurchaseOrderLine` (nav). `AttachmentEntityType.PurchaseOrder` (new enum member, appended last).

- [ ] **Step 1: Update the schema-shape test to expect the new table count (red)**

In `MaterialLedger.Tests/SchemaShapeTests.cs`, change the assertion and rename the test to match:

```csharp
[Fact]
public async Task AllFifteenMaterialTables_Exist()
{
    await using var db = _fixture.CreateContext();
    await using var connection = (SqlConnection)db.Database.GetDbConnection();
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'material'";
    var count = (int)(await command.ExecuteScalarAsync())!;
    Assert.Equal(15, count);
}
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test MaterialLedger.Tests --filter AllFifteenMaterialTables_Exist`
(Requires `MATERIALLEDGER_TEST_ADMIN_CONNECTION` set, per `SqlServerTestDatabaseFixture`.)
Expected: FAIL — actual count is 13.

- [ ] **Step 3: Add the `PurchaseOrder` / `PurchaseOrderLine` models**

Create `MaterialLedger.Data/Models/PurchaseOrder.cs`:

```csharp
namespace MaterialLedger.Data.Models;

public enum PurchaseOrderLineStatus { Open, PartiallyReceived, Received, Cancelled }

public class PurchaseOrder
{
    public long PurchaseOrderId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public string PurchaseOrderNumber { get; set; } = string.Empty;
    public DateOnly? OrderDate { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedBy { get; set; }
    public AppUser? CreatedByUser { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];
}

public class PurchaseOrderLine
{
    public long PurchaseOrderLineId { get; set; }
    public long PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public int LineNumber { get; set; }
    public long MaterialSpecId { get; set; }
    public MaterialSpec? MaterialSpec { get; set; }
    public string? VendorDescription { get; set; }
    public string? VendorPartNumber { get; set; }
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityReceived { get; set; }
    public decimal? UsableWidthIn { get; set; }
    public decimal? UsableLengthIn { get; set; }
    public decimal? UnitPrice { get; set; }
    public DateOnly? QuotedDeliveryDate { get; set; }
    public PurchaseOrderLineStatus Status { get; set; }
}
```

- [ ] **Step 4: Link `SheetLot` to the line it fulfilled**

In `MaterialLedger.Data/Models/SheetLot.cs`, add after `HeatLotNumber`:

```csharp
    public long? PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
```

- [ ] **Step 5: Extend the attachment entity-type enum**

In `MaterialLedger.Data/Models/Attachment.cs`, change:

```csharp
public enum AttachmentEntityType { InventoryItem, MaterialReservation, MaterialTransaction, MaterialException, PurchaseOrder }
```

- [ ] **Step 6: Register the new DbSets and entity configuration**

In `MaterialLedger.Data/MaterialLedgerDbContext.cs`, add two DbSet properties right after `public DbSet<SheetLot> SheetLots => Set<SheetLot>();`:

```csharp
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
```

Inside the existing `modelBuilder.Entity<SheetLot>(e => { ... })` block, add one more line at the end (before the closing `});`):

```csharp
            e.HasOne(x => x.PurchaseOrderLine).WithMany().HasForeignKey(x => x.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
```

Add two new entity configuration blocks (placed after the `SheetLot` block, before `InventoryItem`):

```csharp
        modelBuilder.Entity<PurchaseOrder>(e =>
        {
            e.HasKey(x => x.PurchaseOrderId);
            e.Property(x => x.VendorName).HasMaxLength(200).IsRequired();
            e.Property(x => x.PurchaseOrderNumber).HasMaxLength(100).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurchaseOrderLine>(e =>
        {
            e.HasKey(x => x.PurchaseOrderLineId);
            e.Property(x => x.VendorDescription).HasMaxLength(200);
            e.Property(x => x.VendorPartNumber).HasMaxLength(100);
            e.Property(x => x.QuantityOrdered).HasColumnType("decimal(12,3)").IsRequired();
            e.Property(x => x.QuantityReceived).HasColumnType("decimal(12,3)").IsRequired();
            e.Property(x => x.UsableWidthIn).HasColumnType("decimal(10,3)");
            e.Property(x => x.UsableLengthIn).HasColumnType("decimal(10,3)");
            e.Property(x => x.UnitPrice).HasColumnType("decimal(12,4)");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.PurchaseOrderId, x.LineNumber }).IsUnique();
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_PurchaseOrderLine_QuantityOrdered",
                    "[QuantityOrdered] = FLOOR([QuantityOrdered]) AND [QuantityOrdered] >= 1");
                t.HasCheckConstraint("CK_PurchaseOrderLine_QuantityReceived",
                    "[QuantityReceived] >= 0 AND [QuantityReceived] <= [QuantityOrdered]");
            });
            e.HasOne(x => x.PurchaseOrder).WithMany(x => x.Lines).HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.MaterialSpec).WithMany().HasForeignKey(x => x.MaterialSpecId).OnDelete(DeleteBehavior.Restrict);
        });
```

- [ ] **Step 7: Generate the EF migration**

Run: `dotnet ef migrations add AddPurchaseOrders --project MaterialLedger.Data --startup-project MaterialLedger.Web`

Open the generated file under `MaterialLedger.Data/Migrations/` and confirm it: creates `material.PurchaseOrders` and `material.PurchaseOrderLines` tables with the check constraints and unique index above, adds a nullable `PurchaseOrderLineId` column + FK to `material.SheetLots`, and that `MaterialLedgerDbContextModelSnapshot.cs` was updated alongside it.

- [ ] **Step 8: Apply the migration to the local dev/test database and confirm the schema test passes**

Run: `dotnet test MaterialLedger.Tests --filter AllFifteenMaterialTables_Exist`
Expected: PASS (count is now 15). `SqlServerTestDatabaseFixture` re-provisions the test database and calls `Database.MigrateAsync()` per test class run, so no manual `dotnet ef database update` is needed for the test DB.

- [ ] **Step 9: Add the permissions grant file**

Create `docs/schema/material-permissions-v8.sql`:

```sql
-- docs/schema/material-permissions-v8.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, purchase order
-- entry and receiving-against-PO.
--
-- Grants the reads/inserts/updates PurchaseOrderService and the ReceiveMaterialService PO-line
-- extension need, plus a first-time grant for material.Attachments (the table has existed since
-- v1 but has never been granted — this is the first feature to actually write to it).
--
-- Run against the real `MaterialLedger` database only after the AddPurchaseOrders EF migration
-- has been applied, using an account with GRANT rights. This is a manual administrative step; the
-- application never applies its own schema or permission changes.

USE MaterialLedger;
GO

GRANT SELECT, INSERT, UPDATE ON material.PurchaseOrders TO materialledger_app;
GRANT SELECT, INSERT, UPDATE ON material.PurchaseOrderLines TO materialledger_app;
GRANT SELECT, INSERT ON material.Attachments TO materialledger_app;
GO
```

- [ ] **Step 10: Full build check, then commit**

Run: `dotnet build MaterialLedger.sln`
Expected: builds with no errors.

```bash
git add MaterialLedger.Data/Models/PurchaseOrder.cs MaterialLedger.Data/Models/SheetLot.cs MaterialLedger.Data/Models/Attachment.cs MaterialLedger.Data/MaterialLedgerDbContext.cs MaterialLedger.Data/Migrations/ docs/schema/material-permissions-v8.sql MaterialLedger.Tests/SchemaShapeTests.cs
git commit -m "feat: add PurchaseOrder/PurchaseOrderLine schema and SheetLot PO link"
```

---

### Task 2: `PurchaseOrderService.CreatePurchaseOrderAsync`

**Files:**
- Create: `MaterialLedger.Data/PurchaseOrders/PurchaseOrderService.cs`
- Modify: `MaterialLedger.Web/Program.cs`
- Test: Create `MaterialLedger.Tests/PurchaseOrderServiceTests.cs`

**Interfaces:**
- Consumes: `MaterialLedgerDbContext`, `OperationLogGuard` (from Task 1 / existing).
- Produces: `PurchaseOrderLineRequest(long MaterialSpecId, string? VendorDescription, string? VendorPartNumber, decimal QuantityOrdered, decimal? UsableWidthIn, decimal? UsableLengthIn, decimal? UnitPrice, DateOnly? QuotedDeliveryDate)`, `CreatePurchaseOrderRequest(string VendorName, string PurchaseOrderNumber, DateOnly? OrderDate, string? Notes, IReadOnlyList<PurchaseOrderLineRequest> Lines)`, `PurchaseOrderService.CreatePurchaseOrderAsync(Guid correlationId, CreatePurchaseOrderRequest request, int createdBy) : Task<long>` (returns the new `PurchaseOrderId`) — used by Task 7.

- [ ] **Step 1: Write the failing integration test**

Create `MaterialLedger.Tests/PurchaseOrderServiceTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test MaterialLedger.Tests --filter PurchaseOrderServiceTests`
Expected: FAIL to compile — `MaterialLedger.Data.PurchaseOrders.PurchaseOrderService` doesn't exist yet.

- [ ] **Step 3: Implement `PurchaseOrderService`**

Create `MaterialLedger.Data/PurchaseOrders/PurchaseOrderService.cs`:

```csharp
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.PurchaseOrders;

public record PurchaseOrderLineRequest(
    long MaterialSpecId,
    string? VendorDescription,
    string? VendorPartNumber,
    decimal QuantityOrdered,
    decimal? UsableWidthIn,
    decimal? UsableLengthIn,
    decimal? UnitPrice,
    DateOnly? QuotedDeliveryDate);

public record CreatePurchaseOrderRequest(
    string VendorName,
    string PurchaseOrderNumber,
    DateOnly? OrderDate,
    string? Notes,
    IReadOnlyList<PurchaseOrderLineRequest> Lines);

public class PurchaseOrderService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    public async Task<long> CreatePurchaseOrderAsync(Guid correlationId, CreatePurchaseOrderRequest request, int createdBy)
    {
        Validate(request);

        var payloadHash = HashPayload(request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "CreatePurchaseOrder", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replayId = JsonSerializer.Deserialize<long>(outcome.ResultSummary!);
            await transaction.CommitAsync();
            return replayId;
        }

        var order = new PurchaseOrder
        {
            VendorName = request.VendorName.Trim(),
            PurchaseOrderNumber = request.PurchaseOrderNumber.Trim(),
            OrderDate = request.OrderDate,
            Notes = request.Notes,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = createdBy
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        var lineNumber = 1;
        foreach (var line in request.Lines)
        {
            db.PurchaseOrderLines.Add(new PurchaseOrderLine
            {
                PurchaseOrderId = order.PurchaseOrderId,
                LineNumber = lineNumber++,
                MaterialSpecId = line.MaterialSpecId,
                VendorDescription = line.VendorDescription,
                VendorPartNumber = line.VendorPartNumber,
                QuantityOrdered = line.QuantityOrdered,
                QuantityReceived = 0,
                UsableWidthIn = line.UsableWidthIn,
                UsableLengthIn = line.UsableLengthIn,
                UnitPrice = line.UnitPrice,
                QuotedDeliveryDate = line.QuotedDeliveryDate,
                Status = PurchaseOrderLineStatus.Open
            });
        }
        await db.SaveChangesAsync();

        operationLog.Record(correlationId, "CreatePurchaseOrder", payloadHash, JsonSerializer.Serialize(order.PurchaseOrderId), createdBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return order.PurchaseOrderId;
    }

    private static void Validate(CreatePurchaseOrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.VendorName))
            throw new ArgumentException("Vendor name is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PurchaseOrderNumber))
            throw new ArgumentException("Purchase order number is required.", nameof(request));
        if (request.Lines.Count == 0)
            throw new ArgumentException("A purchase order must have at least one line.", nameof(request));
        foreach (var line in request.Lines)
        {
            if (line.QuantityOrdered < 1 || line.QuantityOrdered != decimal.Truncate(line.QuantityOrdered))
                throw new ArgumentException("Each line's ordered quantity must be a whole number of at least 1.", nameof(request));
        }
    }

    private static string HashPayload<T>(T payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
}
```

- [ ] **Step 4: Run the tests to confirm they pass**

Run: `dotnet test MaterialLedger.Tests --filter PurchaseOrderServiceTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Register the service in DI**

In `MaterialLedger.Web/Program.cs`, add after `builder.Services.AddScoped<MaterialLedger.Data.Receiving.ReceiveMaterialService>();`:

```csharp
builder.Services.AddScoped<MaterialLedger.Data.PurchaseOrders.PurchaseOrderService>();
```

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Data/PurchaseOrders/PurchaseOrderService.cs MaterialLedger.Web/Program.cs MaterialLedger.Tests/PurchaseOrderServiceTests.cs
git commit -m "feat: add PurchaseOrderService.CreatePurchaseOrderAsync"
```

---

### Task 3: `PurchaseOrderService.CancelLineAsync` / `CloseRemainingAsync`

**Files:**
- Modify: `MaterialLedger.Data/PurchaseOrders/PurchaseOrderService.cs`
- Test: Modify `MaterialLedger.Tests/PurchaseOrderServiceTests.cs`

**Interfaces:**
- Produces: `PurchaseOrderService.CancelLineAsync(Guid correlationId, long purchaseOrderLineId, int cancelledBy) : Task<int>`, `PurchaseOrderService.CloseRemainingAsync(Guid correlationId, long purchaseOrderId, int closedBy) : Task<int>` — both used by Task 8.

- [ ] **Step 1: Write the failing tests**

Append to `MaterialLedger.Tests/PurchaseOrderServiceTests.cs` (inside the class, before the closing brace):

```csharp
    private async Task<long> SeedLineAsync(MaterialLedgerDbContext db, int userId, long specId, decimal quantityOrdered, decimal quantityReceived = 0)
    {
        var order = new PurchaseOrder
        {
            VendorName = "Acme Steel", PurchaseOrderNumber = $"PO-{Guid.NewGuid():N}"[..20],
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
        (await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == partialLineId)).PurchaseOrderId = purchaseOrderId;
        (await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == receivedLineId)).PurchaseOrderId = purchaseOrderId;
        await db.SaveChangesAsync();
        var service = new PurchaseOrderService(db, new OperationLogGuard(db));

        var cancelledCount = await service.CloseRemainingAsync(Guid.NewGuid(), purchaseOrderId, userId);

        Assert.Equal(2, cancelledCount);
        Assert.Equal(PurchaseOrderLineStatus.Cancelled, (await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == openLineId)).Status);
        Assert.Equal(PurchaseOrderLineStatus.Cancelled, (await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == partialLineId)).Status);
        Assert.Equal(PurchaseOrderLineStatus.Received, (await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == receivedLineId)).Status);
    }
```

- [ ] **Step 2: Run to confirm it fails**

Run: `dotnet test MaterialLedger.Tests --filter PurchaseOrderServiceTests`
Expected: FAIL to compile — `CancelLineAsync`/`CloseRemainingAsync` don't exist yet.

- [ ] **Step 3: Implement both methods**

In `MaterialLedger.Data/PurchaseOrders/PurchaseOrderService.cs`, add after `CreatePurchaseOrderAsync` (before the `Validate` method):

```csharp
    public async Task<int> CancelLineAsync(Guid correlationId, long purchaseOrderLineId, int cancelledBy)
    {
        var payloadHash = HashPayload(new { purchaseOrderLineId });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "CancelPurchaseOrderLine", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replayCount = JsonSerializer.Deserialize<int>(outcome.ResultSummary!);
            await transaction.CommitAsync();
            return replayCount;
        }

        var line = await db.PurchaseOrderLines.FindAsync(purchaseOrderLineId)
            ?? throw new InvalidOperationException($"Purchase order line {purchaseOrderLineId} does not exist.");
        CancelLineCore(line);
        await db.SaveChangesAsync();

        operationLog.Record(correlationId, "CancelPurchaseOrderLine", payloadHash, JsonSerializer.Serialize(1), cancelledBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return 1;
    }

    public async Task<int> CloseRemainingAsync(Guid correlationId, long purchaseOrderId, int closedBy)
    {
        var payloadHash = HashPayload(new { purchaseOrderId });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "ClosePurchaseOrderRemaining", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replayCount = JsonSerializer.Deserialize<int>(outcome.ResultSummary!);
            await transaction.CommitAsync();
            return replayCount;
        }

        var openLines = await db.PurchaseOrderLines
            .Where(x => x.PurchaseOrderId == purchaseOrderId
                && (x.Status == PurchaseOrderLineStatus.Open || x.Status == PurchaseOrderLineStatus.PartiallyReceived))
            .ToListAsync();
        foreach (var line in openLines)
            CancelLineCore(line);
        await db.SaveChangesAsync();

        operationLog.Record(correlationId, "ClosePurchaseOrderRemaining", payloadHash, JsonSerializer.Serialize(openLines.Count), closedBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return openLines.Count;
    }

    private static void CancelLineCore(PurchaseOrderLine line)
    {
        if (line.Status == PurchaseOrderLineStatus.Received)
            throw new InvalidOperationException($"Purchase order line {line.PurchaseOrderLineId} is already fully received and cannot be cancelled.");
        line.Status = PurchaseOrderLineStatus.Cancelled;
    }
```

- [ ] **Step 4: Run the tests to confirm they pass**

Run: `dotnet test MaterialLedger.Tests --filter PurchaseOrderServiceTests`
Expected: PASS (8 tests total).

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Data/PurchaseOrders/PurchaseOrderService.cs MaterialLedger.Tests/PurchaseOrderServiceTests.cs
git commit -m "feat: add PurchaseOrderService cancel-line and close-remaining"
```

---

### Task 4: `ReceiveMaterialService` — receive against a PO line

**Files:**
- Modify: `MaterialLedger.Data/Receiving/ReceiveMaterialService.cs`
- Test: Modify `MaterialLedger.Tests/ReceiveMaterialServiceTests.cs`

**Interfaces:**
- Consumes: `PurchaseOrderLine`, `PurchaseOrderLineStatus` (Task 1).
- Produces: `ReceiveMaterialRequest` gains a trailing optional `long? PurchaseOrderLineId = null` parameter — used by Task 9.

- [ ] **Step 1: Write the failing tests**

Append to `MaterialLedger.Tests/ReceiveMaterialServiceTests.cs` (inside the class, before the closing brace), and add `using MaterialLedger.Data.PurchaseOrders;` to the top of the file:

```csharp
    private async Task<long> SeedPurchaseOrderLineAsync(MaterialLedgerDbContext db, long specId, decimal quantityOrdered, int userId)
    {
        var order = new PurchaseOrder
        {
            VendorName = "Acme Steel", PurchaseOrderNumber = $"PO-{Guid.NewGuid():N}"[..20],
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = userId
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        var line = new PurchaseOrderLine
        {
            PurchaseOrderId = order.PurchaseOrderId, LineNumber = 1, MaterialSpecId = specId,
            QuantityOrdered = quantityOrdered, QuantityReceived = 0, Status = PurchaseOrderLineStatus.Open
        };
        db.PurchaseOrderLines.Add(line);
        await db.SaveChangesAsync();
        return line.PurchaseOrderLineId;
    }

    [Fact]
    public async Task ReceiveMaterialAsync_AgainstPurchaseOrderLine_PartialReceiptSetsPartiallyReceived()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var lineId = await SeedPurchaseOrderLineAsync(db, specId, quantityOrdered: 10m, userId);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, quantity: 4m) with { PurchaseOrderLineId = lineId };

        var result = await service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId);

        var line = await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == lineId);
        Assert.Equal(4m, line.QuantityReceived);
        Assert.Equal(PurchaseOrderLineStatus.PartiallyReceived, line.Status);

        var lot = await db.SheetLots.SingleAsync(x => x.SheetLotId == result.SheetLotId);
        Assert.Equal(lineId, lot.PurchaseOrderLineId);
    }

    [Fact]
    public async Task ReceiveMaterialAsync_AgainstPurchaseOrderLine_FullReceiptSetsReceived()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var lineId = await SeedPurchaseOrderLineAsync(db, specId, quantityOrdered: 6m, userId);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, quantity: 6m) with { PurchaseOrderLineId = lineId };

        await service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId);

        var line = await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == lineId);
        Assert.Equal(6m, line.QuantityReceived);
        Assert.Equal(PurchaseOrderLineStatus.Received, line.Status);
    }

    [Fact]
    public async Task ReceiveMaterialAsync_ExceedsRemainingPurchaseOrderQuantity_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var lineId = await SeedPurchaseOrderLineAsync(db, specId, quantityOrdered: 3m, userId);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, quantity: 4m) with { PurchaseOrderLineId = lineId };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task ReceiveMaterialAsync_AgainstCancelledPurchaseOrderLine_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var lineId = await SeedPurchaseOrderLineAsync(db, specId, quantityOrdered: 3m, userId);
        var line = await db.PurchaseOrderLines.SingleAsync(x => x.PurchaseOrderLineId == lineId);
        line.Status = PurchaseOrderLineStatus.Cancelled;
        await db.SaveChangesAsync();
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, quantity: 1m) with { PurchaseOrderLineId = lineId };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId));
    }
```

- [ ] **Step 2: Run to confirm it fails**

Run: `dotnet test MaterialLedger.Tests --filter ReceiveMaterialServiceTests`
Expected: FAIL to compile — `ReceiveMaterialRequest` has no `PurchaseOrderLineId` member yet.

- [ ] **Step 3: Extend `ReceiveMaterialService`**

Replace the full contents of `MaterialLedger.Data/Receiving/ReceiveMaterialService.cs`:

```csharp
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MaterialLedger.Data.Receiving;

public record ReceiveMaterialRequest(
    long InventoryLocationId,
    long MaterialSpecId,
    InventoryItemKind ItemKind,
    decimal Quantity,
    decimal? UsableWidthIn,
    decimal? UsableLengthIn,
    ShapeClassification ShapeClassification,
    bool IsQuarantined,
    string? VendorName,
    string PurchaseOrderNumber,
    string? ReceiptNumber,
    string? VendorPartNumber,
    string? HeatLotNumber,
    decimal? CostPerUnit,
    string? Notes,
    long? PurchaseOrderLineId = null);

public class ReceiveMaterialResult
{
    public required long InventoryItemId { get; init; }
    public required long SheetLotId { get; init; }
    public required string PublicItemCode { get; init; }
}

public class ReceiveMaterialService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    private static readonly InventoryLocationType[] LeafLocationTypes =
    [
        InventoryLocationType.Shelf, InventoryLocationType.Bay, InventoryLocationType.Staging,
        InventoryLocationType.Quarantine, InventoryLocationType.Scrap
    ];

    public async Task<ReceiveMaterialResult> ReceiveMaterialAsync(Guid correlationId, ReceiveMaterialRequest request, int receivedBy)
    {
        Validate(request);

        var payloadHash = HashPayload(request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "ReceiveMaterial", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<ReceiveMaterialResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var location = await db.InventoryLocations.FindAsync(request.InventoryLocationId)
            ?? throw new InvalidOperationException($"Location {request.InventoryLocationId} does not exist.");
        if (!LeafLocationTypes.Contains(location.LocationType))
            throw new InvalidOperationException(
                $"Location '{location.LocationCode}' is a {location.LocationType} — inventory can only be recorded at a leaf location.");

        PurchaseOrderLine? purchaseOrderLine = null;
        if (request.PurchaseOrderLineId is not null)
        {
            purchaseOrderLine = await db.PurchaseOrderLines.FindAsync(request.PurchaseOrderLineId.Value)
                ?? throw new InvalidOperationException($"Purchase order line {request.PurchaseOrderLineId} does not exist.");
            if (purchaseOrderLine.Status is PurchaseOrderLineStatus.Cancelled or PurchaseOrderLineStatus.Received)
                throw new InvalidOperationException(
                    $"Purchase order line {purchaseOrderLine.PurchaseOrderLineId} is {purchaseOrderLine.Status} and cannot receive more material.");
            var remaining = purchaseOrderLine.QuantityOrdered - purchaseOrderLine.QuantityReceived;
            if (request.Quantity > remaining)
                throw new InvalidOperationException(
                    $"Cannot receive {request.Quantity} — only {remaining} remains outstanding on purchase order line {purchaseOrderLine.PurchaseOrderLineId}.");
        }

        var receivedAt = DateTime.UtcNow;
        var sheetLot = new SheetLot
        {
            MaterialSpecId = request.MaterialSpecId,
            VendorName = request.VendorName,
            PurchaseOrderNumber = request.PurchaseOrderNumber,
            ReceiptNumber = request.ReceiptNumber,
            VendorPartNumber = request.VendorPartNumber,
            HeatLotNumber = request.HeatLotNumber,
            CostPerUnit = request.CostPerUnit,
            ReceivedAtUtc = receivedAt,
            SourceType = SheetLotSourceType.Receipt,
            PurchaseOrderLineId = purchaseOrderLine?.PurchaseOrderLineId
        };
        db.SheetLots.Add(sheetLot);
        await db.SaveChangesAsync();

        long nextSeq;
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT NEXT VALUE FOR material.ItemCodeSeq";
            command.Transaction = (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
            var scalarResult = await command.ExecuteScalarAsync();
            nextSeq = (long)scalarResult!;
        }
        var publicItemCode = $"SHEET-{nextSeq}";

        var item = new InventoryItem
        {
            PublicItemCode = publicItemCode,
            MaterialSpecId = request.MaterialSpecId,
            SheetLotId = sheetLot.SheetLotId,
            InventoryLocationId = request.InventoryLocationId,
            ItemKind = request.ItemKind,
            Status = request.IsQuarantined ? InventoryItemStatus.Quarantined : InventoryItemStatus.Available,
            QuantityOnHand = request.Quantity,
            UsableWidthIn = request.UsableWidthIn,
            UsableLengthIn = request.UsableLengthIn,
            ShapeClassification = request.ShapeClassification,
            LastObservedAtUtc = receivedAt,
            LastObservedBy = receivedBy,
            Notes = request.Notes
        };
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        db.MaterialTransactions.Add(new MaterialTransaction
        {
            OccurredAtUtc = receivedAt,
            RecordedAtUtc = receivedAt,
            RecordedBy = receivedBy,
            TransactionType = MaterialTransactionType.Receipt,
            InventoryItemId = item.InventoryItemId,
            SheetLotId = sheetLot.SheetLotId,
            QuantityDelta = request.Quantity,
            GeometryWidthInSnapshot = request.UsableWidthIn,
            GeometryLengthInSnapshot = request.UsableLengthIn,
            Notes = request.Notes,
            SourceSystem = MaterialTransactionSourceSystem.OperatorUI
        });

        if (purchaseOrderLine is not null)
        {
            purchaseOrderLine.QuantityReceived += request.Quantity;
            purchaseOrderLine.Status = purchaseOrderLine.QuantityReceived >= purchaseOrderLine.QuantityOrdered
                ? PurchaseOrderLineStatus.Received
                : PurchaseOrderLineStatus.PartiallyReceived;
        }

        var result = new ReceiveMaterialResult
        {
            InventoryItemId = item.InventoryItemId,
            SheetLotId = sheetLot.SheetLotId,
            PublicItemCode = publicItemCode
        };
        operationLog.Record(correlationId, "ReceiveMaterial", payloadHash, JsonSerializer.Serialize(result), receivedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static void Validate(ReceiveMaterialRequest request)
    {
        if (request.ItemKind == InventoryItemKind.Drop)
            throw new ArgumentException("ReceiveMaterial only accepts full sheets or bundles, not drops.", nameof(request));
        if (request.Quantity < 1 || request.Quantity != decimal.Truncate(request.Quantity))
            throw new ArgumentException("Received quantity must be a whole number of at least 1 sheet.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PurchaseOrderNumber))
            throw new ArgumentException("A purchase order number is required to receive material.", nameof(request));
    }

    private static string HashPayload(ReceiveMaterialRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
}
```

- [ ] **Step 4: Run the tests to confirm they pass**

Run: `dotnet test MaterialLedger.Tests --filter ReceiveMaterialServiceTests`
Expected: PASS (11 tests total — 7 existing + 4 new).

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Data/Receiving/ReceiveMaterialService.cs MaterialLedger.Tests/ReceiveMaterialServiceTests.cs
git commit -m "feat: let ReceiveMaterialService consume against a purchase order line"
```

---

### Task 5: `AttachmentStorageService`

**Files:**
- Create: `MaterialLedger.Data/Attachments/AttachmentStorageService.cs`
- Modify: `MaterialLedger.Web/Program.cs`
- Test: Create `MaterialLedger.Tests/AttachmentStorageServiceTests.cs`

**Interfaces:**
- Consumes: `Attachment`, `AttachmentEntityType` (existing/Task 1).
- Produces: `AttachmentStorageService.SaveAsync(AttachmentEntityType entityType, long entityId, string fileName, string? contentType, Stream content, int uploadedBy) : Task<Attachment>`, `AttachmentStorageService.GetFullPath(Attachment attachment) : string` — used by Task 8.

- [ ] **Step 1: Write the failing test**

Create `MaterialLedger.Tests/AttachmentStorageServiceTests.cs`:

```csharp
using System.Text;
using MaterialLedger.Data;
using MaterialLedger.Data.Attachments;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class AttachmentStorageServiceTests : IClassFixture<SqlServerTestDatabaseFixture>, IDisposable
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"ml-attachments-{Guid.NewGuid():N}");

    public AttachmentStorageServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true);
    }

    [Fact]
    public async Task SaveAsync_WritesFileToDiskAndInsertsAttachmentRow()
    {
        await using var db = _fixture.CreateContext();
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Programmer, IsActive = true, PasswordHash = "x" };
        db.Add(user);
        await db.SaveChangesAsync();

        var service = new AttachmentStorageService(db, _storageRoot);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("fake pdf bytes"));

        var attachment = await service.SaveAsync(AttachmentEntityType.PurchaseOrder, 42, "confirmation.pdf", "application/pdf", content, user.AppUserId);

        Assert.True(File.Exists(service.GetFullPath(attachment)));
        Assert.EndsWith(".pdf", attachment.StoragePath);
        Assert.NotNull(attachment.Checksum);

        var stored = await db.Attachments.SingleAsync(x => x.AttachmentId == attachment.AttachmentId);
        Assert.Equal(AttachmentEntityType.PurchaseOrder, stored.EntityType);
        Assert.Equal(42, stored.EntityId);
        Assert.Equal("application/pdf", stored.ContentType);
    }
}
```

- [ ] **Step 2: Run to confirm it fails**

Run: `dotnet test MaterialLedger.Tests --filter AttachmentStorageServiceTests`
Expected: FAIL to compile — `AttachmentStorageService` doesn't exist yet.

- [ ] **Step 3: Implement `AttachmentStorageService`**

Create `MaterialLedger.Data/Attachments/AttachmentStorageService.cs`:

```csharp
using System.Security.Cryptography;
using MaterialLedger.Data.Models;

namespace MaterialLedger.Data.Attachments;

public class AttachmentStorageService(MaterialLedgerDbContext db, string storageRootPath)
{
    public async Task<Attachment> SaveAsync(
        AttachmentEntityType entityType, long entityId, string fileName, string? contentType, Stream content, int uploadedBy)
    {
        Directory.CreateDirectory(storageRootPath);
        var storedFileName = $"{entityType}-{entityId}-{Guid.NewGuid():N}{Path.GetExtension(fileName)}";
        var fullPath = Path.Combine(storageRootPath, storedFileName);

        await using (var fileStream = File.Create(fullPath))
        {
            await content.CopyToAsync(fileStream);
        }

        var checksum = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullPath)));

        var attachment = new Attachment
        {
            EntityType = entityType,
            EntityId = entityId,
            StoragePath = storedFileName,
            ContentType = contentType,
            Checksum = checksum,
            UploadedAtUtc = DateTime.UtcNow,
            UploadedBy = uploadedBy
        };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();
        return attachment;
    }

    public string GetFullPath(Attachment attachment) => Path.Combine(storageRootPath, attachment.StoragePath);
}
```

- [ ] **Step 4: Run the test to confirm it passes**

Run: `dotnet test MaterialLedger.Tests --filter AttachmentStorageServiceTests`
Expected: PASS.

- [ ] **Step 5: Register the service in DI**

In `MaterialLedger.Web/Program.cs`, add after the `PurchaseOrderService` registration from Task 2:

```csharp
builder.Services.AddScoped(sp => new MaterialLedger.Data.Attachments.AttachmentStorageService(
    sp.GetRequiredService<MaterialLedger.Data.MaterialLedgerDbContext>(),
    builder.Configuration["Storage:AttachmentsPath"] ?? "App_Data/attachments"));
```

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Data/Attachments/AttachmentStorageService.cs MaterialLedger.Web/Program.cs MaterialLedger.Tests/AttachmentStorageServiceTests.cs
git commit -m "feat: add AttachmentStorageService for uploading files against a record"
```

---

### Task 6: `PurchaseOrders/Index` page + status display helper

**Files:**
- Create: `MaterialLedger.Web/Shared/PurchaseOrderStatusDisplay.cs`
- Create: `MaterialLedger.Web/Pages/PurchaseOrders/Index.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/PurchaseOrders/Index.cshtml`
- Test: Create `MaterialLedger.Tests/PurchaseOrderStatusDisplayTests.cs`

**Interfaces:**
- Consumes: `PurchaseOrder`, `PurchaseOrderLine`, `PurchaseOrderLineStatus` (Task 1).
- Produces: `PurchaseOrderStatus` (enum: `Open`, `Received`, `Cancelled`), `PurchaseOrderStatusDisplay.DeriveStatus(IReadOnlyCollection<PurchaseOrderLine> lines) : PurchaseOrderStatus`, `PurchaseOrderStatusDisplay.Label(PurchaseOrderStatus status) : string` — reused by Task 8.

- [ ] **Step 1: Write the failing unit test (no database needed — pure function)**

Create `MaterialLedger.Tests/PurchaseOrderStatusDisplayTests.cs`:

```csharp
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Shared;
using Xunit;

namespace MaterialLedger.Tests;

public class PurchaseOrderStatusDisplayTests
{
    private static PurchaseOrderLine Line(PurchaseOrderLineStatus status) => new() { Status = status };

    [Fact]
    public void DeriveStatus_AllLinesCancelled_ReturnsCancelled()
    {
        var status = PurchaseOrderStatusDisplay.DeriveStatus([Line(PurchaseOrderLineStatus.Cancelled), Line(PurchaseOrderLineStatus.Cancelled)]);
        Assert.Equal(PurchaseOrderStatus.Cancelled, status);
    }

    [Fact]
    public void DeriveStatus_AnyLineOpenOrPartial_ReturnsOpen()
    {
        var status = PurchaseOrderStatusDisplay.DeriveStatus([Line(PurchaseOrderLineStatus.Received), Line(PurchaseOrderLineStatus.PartiallyReceived)]);
        Assert.Equal(PurchaseOrderStatus.Open, status);
    }

    [Fact]
    public void DeriveStatus_AllLinesReceivedOrCancelledWithAtLeastOneReceived_ReturnsReceived()
    {
        var status = PurchaseOrderStatusDisplay.DeriveStatus([Line(PurchaseOrderLineStatus.Received), Line(PurchaseOrderLineStatus.Cancelled)]);
        Assert.Equal(PurchaseOrderStatus.Received, status);
    }
}
```

- [ ] **Step 2: Run to confirm it fails**

Run: `dotnet test MaterialLedger.Tests --filter PurchaseOrderStatusDisplayTests`
Expected: FAIL to compile — `PurchaseOrderStatusDisplay` doesn't exist yet.

- [ ] **Step 3: Implement the display helper**

Create `MaterialLedger.Web/Shared/PurchaseOrderStatusDisplay.cs`:

```csharp
using MaterialLedger.Data.Models;

namespace MaterialLedger.Web.Shared;

public enum PurchaseOrderStatus { Open, Received, Cancelled }

public static class PurchaseOrderStatusDisplay
{
    public static PurchaseOrderStatus DeriveStatus(IReadOnlyCollection<PurchaseOrderLine> lines)
    {
        if (lines.Count == 0 || lines.All(x => x.Status == PurchaseOrderLineStatus.Cancelled))
            return PurchaseOrderStatus.Cancelled;
        if (lines.Any(x => x.Status is PurchaseOrderLineStatus.Open or PurchaseOrderLineStatus.PartiallyReceived))
            return PurchaseOrderStatus.Open;
        return PurchaseOrderStatus.Received;
    }

    public static string Label(PurchaseOrderStatus status) => status switch
    {
        PurchaseOrderStatus.Open => "Open",
        PurchaseOrderStatus.Received => "Received",
        PurchaseOrderStatus.Cancelled => "Cancelled",
        _ => "Unknown"
    };
}
```

- [ ] **Step 4: Run the test to confirm it passes**

Run: `dotnet test MaterialLedger.Tests --filter PurchaseOrderStatusDisplayTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Add the Index page model**

Create `MaterialLedger.Web/Pages/PurchaseOrders/Index.cshtml.cs`:

```csharp
// MaterialLedger.Web/Pages/PurchaseOrders/Index.cshtml.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.PurchaseOrders;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class IndexModel(MaterialLedgerDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string StatusFilter { get; set; } = "Open";
    public List<PurchaseOrderRow> Orders { get; set; } = [];

    public async Task OnGetAsync()
    {
        var orders = await db.PurchaseOrders
            .Include(x => x.Lines)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync();

        Orders = orders
            .Select(order => new PurchaseOrderRow(order, PurchaseOrderStatusDisplay.DeriveStatus(order.Lines)))
            .Where(row => StatusFilter == "All" || row.Status.ToString() == StatusFilter)
            .ToList();
    }
}

public sealed record PurchaseOrderRow(PurchaseOrder Order, PurchaseOrderStatus Status);
```

- [ ] **Step 6: Add the Index view**

Create `MaterialLedger.Web/Pages/PurchaseOrders/Index.cshtml`:

```html
@* MaterialLedger.Web/Pages/PurchaseOrders/Index.cshtml *@
@page
@model MaterialLedger.Web.Pages.PurchaseOrders.IndexModel
@using MaterialLedger.Web.Shared
@{ Layout = "Shared/_Layout"; }
<header class="page-heading">
    <p class="eyebrow">Inventory setup</p>
    <h1>Purchase orders</h1>
    <p><a asp-page="Create">Order material →</a></p>
</header>
<p>
    <a asp-page="Index" asp-route-statusFilter="Open">Open</a>
    | <a asp-page="Index" asp-route-statusFilter="All">All</a>
</p>
<table>
    <thead><tr><th>PO #</th><th>Vendor</th><th>Order date</th><th>Lines</th><th>Status</th></tr></thead>
    <tbody>
    @foreach (var row in Model.Orders)
    {
        <tr>
            <td><a asp-page="Details" asp-route-id="@row.Order.PurchaseOrderId">@row.Order.PurchaseOrderNumber</a></td>
            <td>@row.Order.VendorName</td>
            <td>@(row.Order.OrderDate?.ToString("d") ?? "—")</td>
            <td>@row.Order.Lines.Count</td>
            <td>@PurchaseOrderStatusDisplay.Label(row.Status)</td>
        </tr>
    }
    </tbody>
</table>
```

- [ ] **Step 7: Build to confirm the Razor view compiles**

Run: `dotnet build MaterialLedger.sln`
Expected: builds with no errors (Razor views compile at build time in this project; a bad `asp-for`/`asp-route-*` binding or missing page would fail here since `Details` doesn't exist yet — that's fine, `asp-page="Details"`/`asp-page="Create"` are resolved at runtime by page discovery, not build time, so this step only catches C#/Razor syntax errors, not missing target pages).

- [ ] **Step 8: Commit**

```bash
git add MaterialLedger.Web/Shared/PurchaseOrderStatusDisplay.cs MaterialLedger.Web/Pages/PurchaseOrders/Index.cshtml.cs MaterialLedger.Web/Pages/PurchaseOrders/Index.cshtml MaterialLedger.Tests/PurchaseOrderStatusDisplayTests.cs
git commit -m "feat: add PurchaseOrders/Index page and status display helper"
```

---

### Task 7: `PurchaseOrders/Create` page

**Files:**
- Create: `MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml`

**Interfaces:**
- Consumes: `PurchaseOrderService.CreatePurchaseOrderAsync` (Task 2), `MaterialSpec` (existing).
- Produces: nothing consumed by later tasks (redirects to `Details`, built in Task 8).

**Note:** Line rows pick from the existing material catalog only (no inline "create a new spec" fields per row) — same simplification the shipped `Receiving/NewReceipt` page already made over the original `ReceiveMaterial` design note, with a link to `Materials/Index` for anything missing. Rows are pre-rendered up to a fixed cap and shown/hidden with plain JS (no client-side row cloning), so Razor Pages' standard `List<T>` index binding (`Lines[i].Property`) works with zero custom binder code — rows left blank are filtered out server-side.

- [ ] **Step 1: Add the Create page model**

Create `MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml.cs`:

```csharp
// MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.PurchaseOrders;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.PurchaseOrders;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class CreateModel(MaterialLedgerDbContext db, PurchaseOrderService purchaseOrderService) : PageModel
{
    private const int MaxLines = 20;

    [BindProperty] public string VendorName { get; set; } = string.Empty;
    [BindProperty] public string PurchaseOrderNumber { get; set; } = string.Empty;
    [BindProperty] public DateOnly? OrderDate { get; set; }
    [BindProperty] public string? Notes { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public List<PurchaseOrderLineInput> Lines { get; set; } = [];

    public List<MaterialSpec> AvailableSpecs { get; set; } = [];
    public string? Error { get; set; }

    public async Task OnGetAsync()
    {
        AvailableSpecs = await LoadAvailableSpecsAsync();
        CorrelationId = Guid.NewGuid();
        Lines = Enumerable.Range(0, MaxLines).Select(_ => new PurchaseOrderLineInput()).ToList();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        AvailableSpecs = await LoadAvailableSpecsAsync();

        var lineRequests = new List<PurchaseOrderLineRequest>();
        foreach (var line in Lines)
        {
            if (line.ExistingMaterialSpecId is null && line.QuantityOrdered <= 0) continue;
            if (line.ExistingMaterialSpecId is null)
            {
                Error = "Choose a material and thickness for every line you fill in.";
                return Page();
            }
            lineRequests.Add(new PurchaseOrderLineRequest(
                line.ExistingMaterialSpecId.Value, line.VendorDescription, line.VendorPartNumber,
                line.QuantityOrdered, line.UsableWidthIn, line.UsableLengthIn, line.UnitPrice, line.QuotedDeliveryDate));
        }

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new CreatePurchaseOrderRequest(VendorName, PurchaseOrderNumber, OrderDate, Notes, lineRequests);
            var purchaseOrderId = await purchaseOrderService.CreatePurchaseOrderAsync(CorrelationId, request, userId);
            return RedirectToPage("Details", new { id = purchaseOrderId });
        }
        catch (Exception ex) when (ex is ArgumentException or OperationConflictException)
        {
            Error = ex is OperationConflictException
                ? "This form was already submitted with different values — please review and resubmit."
                : ex.Message;
            return Page();
        }
    }

    private Task<List<MaterialSpec>> LoadAvailableSpecsAsync() => db.MaterialSpecs
        .AsNoTracking()
        .Where(x => x.IsActive)
        .OrderBy(x => x.MaterialFamily).ThenBy(x => x.Grade).ThenBy(x => x.Finish).ThenBy(x => x.ThicknessIn)
        .ToListAsync();
}

public class PurchaseOrderLineInput
{
    public long? ExistingMaterialSpecId { get; set; }
    public string? VendorDescription { get; set; }
    public string? VendorPartNumber { get; set; }
    public decimal QuantityOrdered { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? UsableWidthIn { get; set; }
    public decimal? UsableLengthIn { get; set; }
    public DateOnly? QuotedDeliveryDate { get; set; }
}
```

- [ ] **Step 2: Add the Create view**

Create `MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml`:

```html
@* MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml *@
@page
@model MaterialLedger.Web.Pages.PurchaseOrders.CreateModel
@{ Layout = "Shared/_Layout"; }
<header class="page-heading">
    <p class="eyebrow">Inventory setup</p>
    <h1>Order material</h1>
    <p>Record a purchase order so it can be tracked and received against.</p>
</header>

@if (Model.Error is not null)
{
    <p style="color:red">@Model.Error</p>
}

<form method="post">
    @Html.AntiForgeryToken()
    <input type="hidden" asp-for="CorrelationId" />

    <fieldset>
        <legend>Order details</legend>
        <label>Vendor <input asp-for="VendorName" maxlength="200" /></label>
        <label>PO number <input asp-for="PurchaseOrderNumber" maxlength="100" /></label>
        <label>Order date <input asp-for="OrderDate" type="date" /></label>
        <label>Notes <textarea asp-for="Notes"></textarea></label>
    </fieldset>

    @for (var i = 0; i < Model.Lines.Count; i++)
    {
        <fieldset class="po-line" style="@(i > 0 ? "display:none" : "")">
            <legend>Line @(i + 1)</legend>
            <label>Material family / grade / finish
                <select class="po-line-material" required>
                    <option value="">Choose material</option>
                    @foreach (var material in Model.AvailableSpecs.GroupBy(spec => $"{spec.MaterialFamily}|{spec.Grade}|{spec.Finish}"))
                    {
                        var spec = material.First();
                        <option value="@material.Key">@spec.MaterialFamily@(spec.Grade is null ? "" : $" {spec.Grade}")@(spec.Finish is null ? "" : $" {spec.Finish}")</option>
                    }
                </select>
            </label>
            <label>Thickness
                <select asp-for="Lines[i].ExistingMaterialSpecId" class="po-line-thickness" disabled>
                    <option value="">Choose thickness</option>
                    @foreach (var spec in Model.AvailableSpecs)
                    {
                        <option value="@spec.MaterialSpecId" data-material-key="@($"{spec.MaterialFamily}|{spec.Grade}|{spec.Finish}")">@spec.DisplayName</option>
                    }
                </select>
            </label>
            <label>Vendor description <input asp-for="Lines[i].VendorDescription" maxlength="200" /></label>
            <label>Vendor part number <input asp-for="Lines[i].VendorPartNumber" maxlength="100" /></label>
            <label>Quantity ordered <input asp-for="Lines[i].QuantityOrdered" type="number" step="1" min="0" /></label>
            <label>Unit price <input asp-for="Lines[i].UnitPrice" type="number" step="0.01" min="0" /></label>
            <label>Usable width (in) <input asp-for="Lines[i].UsableWidthIn" type="number" step="0.001" /></label>
            <label>Usable length (in) <input asp-for="Lines[i].UsableLengthIn" type="number" step="0.001" /></label>
            <label>Quoted delivery date <input asp-for="Lines[i].QuotedDeliveryDate" type="date" /></label>
        </fieldset>
    }
    <button type="button" id="add-line">Add another line</button>
    <p>Need a material that is not listed? <a asp-page="/Materials/Index">Add it to the material catalog</a>.</p>

    <button type="submit">Create purchase order</button>
</form>
<script>
    document.querySelectorAll('.po-line').forEach(row => {
        const material = row.querySelector('.po-line-material');
        const thickness = row.querySelector('.po-line-thickness');
        const filterThicknesses = reset => {
            for (const option of thickness.options) {
                if (!option.value) continue;
                const matches = option.dataset.materialKey === material.value;
                option.hidden = !matches;
                option.disabled = !matches;
            }
            thickness.disabled = !material.value;
            if (reset) thickness.value = '';
        };
        material.addEventListener('change', () => filterThicknesses(true));
        filterThicknesses(false);
    });

    const lines = Array.from(document.querySelectorAll('.po-line'));
    document.getElementById('add-line').addEventListener('click', () => {
        const next = lines.find(row => row.style.display === 'none');
        if (next) next.style.display = '';
    });
</script>
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build MaterialLedger.sln`
Expected: builds with no errors.

- [ ] **Step 4: Commit**

```bash
git add MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml.cs MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml
git commit -m "feat: add PurchaseOrders/Create page"
```

---

### Task 8: `PurchaseOrders/Details` page (view, receive/cancel/close, attachment upload+download)

**Files:**
- Create: `MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml`

**Interfaces:**
- Consumes: `PurchaseOrderService.CancelLineAsync`/`CloseRemainingAsync` (Task 3), `AttachmentStorageService.SaveAsync`/`GetFullPath` (Task 5), `PurchaseOrderStatusDisplay` (Task 6).
- Produces: the `Receive` link this page renders (`/Receiving/SelectLocation?poLineId={id}`) is consumed by Task 9.

- [ ] **Step 1: Add the Details page model**

Create `MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml.cs`:

```csharp
// MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Attachments;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.PurchaseOrders;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.PurchaseOrders;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class DetailsModel(MaterialLedgerDbContext db, PurchaseOrderService purchaseOrderService, AttachmentStorageService attachmentStorage) : PageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public IFormFile? File { get; set; }

    public PurchaseOrder? Order { get; set; }
    public List<Attachment> Attachments { get; set; } = [];
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return NotFound();
        CorrelationId = Guid.NewGuid();
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAttachmentAsync()
    {
        if (!await LoadAsync()) return NotFound();

        if (File is null || File.Length == 0)
        {
            Error = "Choose a file to upload.";
            return Page();
        }

        await using var stream = File.OpenReadStream();
        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        await attachmentStorage.SaveAsync(AttachmentEntityType.PurchaseOrder, Id, File.FileName, File.ContentType, stream, userId);
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostCancelLineAsync(long lineId)
    {
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await purchaseOrderService.CancelLineAsync(CorrelationId, lineId, userId);
            return RedirectToPage(new { id = Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationConflictException)
        {
            if (!await LoadAsync()) return NotFound();
            Error = ex.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostCloseRemainingAsync()
    {
        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        await purchaseOrderService.CloseRemainingAsync(CorrelationId, Id, userId);
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnGetDownloadAttachmentAsync(long attachmentId)
    {
        if (!await LoadAsync()) return NotFound();

        var attachment = Attachments.FirstOrDefault(x => x.AttachmentId == attachmentId);
        if (attachment is null) return NotFound();

        var path = attachmentStorage.GetFullPath(attachment);
        var downloadFileName = $"{Order!.PurchaseOrderNumber}-confirmation{Path.GetExtension(attachment.StoragePath)}";
        return PhysicalFile(path, attachment.ContentType ?? "application/octet-stream", downloadFileName);
    }

    private async Task<bool> LoadAsync()
    {
        Order = await db.PurchaseOrders
            .Include(x => x.Lines).ThenInclude(x => x.MaterialSpec)
            .FirstOrDefaultAsync(x => x.PurchaseOrderId == Id);
        if (Order is null) return false;

        Attachments = await db.Attachments
            .Where(x => x.EntityType == AttachmentEntityType.PurchaseOrder && x.EntityId == Id)
            .OrderByDescending(x => x.UploadedAtUtc)
            .ToListAsync();
        return true;
    }
}
```

- [ ] **Step 2: Add the Details view**

Create `MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml`:

```html
@* MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml *@
@page "{id:long}"
@model MaterialLedger.Web.Pages.PurchaseOrders.DetailsModel
@using MaterialLedger.Data.Models
@using MaterialLedger.Web.Shared
@{
    Layout = "Shared/_Layout";
    var status = PurchaseOrderStatusDisplay.DeriveStatus(Model.Order!.Lines);
}
<header class="page-heading">
    <p class="eyebrow">Purchase orders</p>
    <h1>@Model.Order.PurchaseOrderNumber — @Model.Order.VendorName</h1>
    <p>Status: @PurchaseOrderStatusDisplay.Label(status) — Ordered @(Model.Order.OrderDate?.ToString("d") ?? "date not recorded")</p>
</header>

@if (Model.Error is not null)
{
    <p style="color:red">@Model.Error</p>
}

<table>
    <thead><tr><th>Line</th><th>Material</th><th>Vendor description</th><th>Ordered</th><th>Received</th><th>Remaining</th><th>Unit price</th><th>Status</th><th></th></tr></thead>
    <tbody>
    @foreach (var line in Model.Order.Lines.OrderBy(x => x.LineNumber))
    {
        <tr>
            <td>@line.LineNumber</td>
            <td>@line.MaterialSpec?.DisplayName</td>
            <td>@line.VendorDescription</td>
            <td>@DisplayFormat.Quantity(line.QuantityOrdered)</td>
            <td>@DisplayFormat.Quantity(line.QuantityReceived)</td>
            <td>@DisplayFormat.Quantity(line.QuantityOrdered - line.QuantityReceived)</td>
            <td>@(line.UnitPrice?.ToString("0.00") ?? "—")</td>
            <td>@line.Status</td>
            <td>
                @if (line.Status is PurchaseOrderLineStatus.Open or PurchaseOrderLineStatus.PartiallyReceived)
                {
                    <a asp-page="/Receiving/SelectLocation" asp-route-poLineId="@line.PurchaseOrderLineId">Receive</a>
                    <form action="/PurchaseOrders/Details/@Model.Order.PurchaseOrderId?handler=CancelLine&amp;lineId=@line.PurchaseOrderLineId" method="post">
                        @Html.AntiForgeryToken()
                        <button type="submit">Cancel line</button>
                    </form>
                }
            </td>
        </tr>
    }
    </tbody>
</table>

@if (status == PurchaseOrderStatus.Open)
{
    <form action="/PurchaseOrders/Details/@Model.Order.PurchaseOrderId?handler=CloseRemaining" method="post">
        @Html.AntiForgeryToken()
        <button type="submit">Close remaining lines</button>
    </form>
}

<section>
    <h2>Vendor confirmation</h2>
    <ul>
    @foreach (var attachment in Model.Attachments)
    {
        <li><a asp-page="/PurchaseOrders/Details" asp-route-id="@Model.Order.PurchaseOrderId" asp-page-handler="DownloadAttachment" asp-route-attachmentId="@attachment.AttachmentId">Vendor confirmation</a> — uploaded @attachment.UploadedAtUtc.ToString("d")</li>
    }
    </ul>
    <form action="/PurchaseOrders/Details/@Model.Order.PurchaseOrderId?handler=UploadAttachment" method="post" enctype="multipart/form-data">
        @Html.AntiForgeryToken()
        <label>Upload confirmation PDF <input type="file" name="File" /></label>
        <button type="submit">Upload</button>
    </form>
</section>

<p><a asp-page="Index">Back to purchase orders</a></p>
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build MaterialLedger.sln`
Expected: builds with no errors.

- [ ] **Step 4: Commit**

```bash
git add MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml.cs MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml
git commit -m "feat: add PurchaseOrders/Details page with receive/cancel/close and attachment upload"
```

---

### Task 9: Wire `poLineId` through the Receiving flow

**Files:**
- Modify: `MaterialLedger.Web/Pages/Receiving/SelectLocation.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Receiving/SelectLocation.cshtml`
- Modify: `MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml`

**Interfaces:**
- Consumes: the `/Receiving/SelectLocation?poLineId={id}` link produced by Task 8; `ReceiveMaterialRequest.PurchaseOrderLineId` (Task 4).

- [ ] **Step 1: Pass `poLineId` through the location picker**

Replace the full contents of `MaterialLedger.Web/Pages/Receiving/SelectLocation.cshtml.cs`:

```csharp
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Receiving;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class SelectLocationModel(MaterialLedgerDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public long? PoLineId { get; set; }
    public List<InventoryLocation> ConfirmedSlots { get; set; } = [];

    public async Task OnGetAsync()
    {
        ConfirmedSlots = await ActiveMaterialSlotQuery.Create(db).ToListAsync();
    }
}
```

Replace the full contents of `MaterialLedger.Web/Pages/Receiving/SelectLocation.cshtml`:

```html
@page
@model MaterialLedger.Web.Pages.Receiving.SelectLocationModel
@{ Layout = "Shared/_Layout"; ViewData["Title"] = "Receive delivery"; }

<h1>Receive delivery</h1>
<p>Choose the confirmed rack slot where this vendor delivery will be received.</p>
<table>
    <thead><tr><th>Rack</th><th>Slot</th><th></th></tr></thead>
    <tbody>
    @foreach (var slot in Model.ConfirmedSlots)
    {
        <tr>
            <td>@slot.ParentInventoryLocation?.DisplayName</td>
            <td>@slot.LocationCode</td>
            <td><a asp-page="NewReceipt" asp-route-locationId="@slot.InventoryLocationId" asp-route-poLineId="@Model.PoLineId">Receive delivery here</a></td>
        </tr>
    }
    </tbody>
</table>
```

- [ ] **Step 2: Pre-fill `NewReceipt` from the PO line when present**

Replace the full contents of `MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml.cs`:

```csharp
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Receiving;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Receiving;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class NewReceiptModel(MaterialLedgerDbContext db, ReceiveMaterialService receiveMaterial) : PageModel
{
    [BindProperty] public long LocationId { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public long? PoLineId { get; set; }
    [BindProperty] public long? ExistingMaterialSpecId { get; set; }
    [BindProperty] public InventoryItemKind ItemKind { get; set; } = InventoryItemKind.FullSheetBundle;
    [BindProperty] public decimal Quantity { get; set; }
    [BindProperty] public decimal? UsableWidthIn { get; set; }
    [BindProperty] public decimal? UsableLengthIn { get; set; }
    [BindProperty] public ShapeClassification ShapeClassification { get; set; } = ShapeClassification.Rectangular;
    [BindProperty] public bool IsQuarantined { get; set; }
    [BindProperty] public string? VendorName { get; set; }
    [BindProperty] public string PurchaseOrderNumber { get; set; } = string.Empty;
    [BindProperty] public string? ReceiptNumber { get; set; }
    [BindProperty] public string? VendorPartNumber { get; set; }
    [BindProperty] public string? HeatLotNumber { get; set; }
    [BindProperty] public decimal? CostPerUnit { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public InventoryLocation? Location { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    public List<MaterialSpec> AvailableSpecs { get; set; } = [];
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long locationId, long? poLineId)
    {
        Location = await db.InventoryLocations.FindAsync(locationId);
        if (Location is null) return NotFound();

        LocationId = locationId;
        PoLineId = poLineId;
        CorrelationId = Guid.NewGuid();
        AvailableSpecs = await LoadAvailableSpecsAsync();

        if (poLineId is not null)
        {
            PurchaseOrderLine = await db.PurchaseOrderLines
                .Include(x => x.PurchaseOrder)
                .FirstOrDefaultAsync(x => x.PurchaseOrderLineId == poLineId);
            if (PurchaseOrderLine is not null)
            {
                ExistingMaterialSpecId = PurchaseOrderLine.MaterialSpecId;
                VendorName = PurchaseOrderLine.PurchaseOrder!.VendorName;
                PurchaseOrderNumber = PurchaseOrderLine.PurchaseOrder.PurchaseOrderNumber;
                VendorPartNumber = PurchaseOrderLine.VendorPartNumber;
                CostPerUnit = PurchaseOrderLine.UnitPrice;
                UsableWidthIn = PurchaseOrderLine.UsableWidthIn;
                UsableLengthIn = PurchaseOrderLine.UsableLengthIn;
            }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Location = await db.InventoryLocations.FindAsync(LocationId);
        AvailableSpecs = await LoadAvailableSpecsAsync();
        if (PoLineId is not null)
        {
            PurchaseOrderLine = await db.PurchaseOrderLines
                .Include(x => x.PurchaseOrder)
                .FirstOrDefaultAsync(x => x.PurchaseOrderLineId == PoLineId);
        }

        if (ExistingMaterialSpecId is null)
        {
            Error = "Choose a material and thickness from the catalog.";
            return Page();
        }

        if (AvailableSpecs.All(s => s.MaterialSpecId != ExistingMaterialSpecId))
        {
            Error = "The selected material is no longer available. Please choose another.";
            return Page();
        }

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new ReceiveMaterialRequest(
                InventoryLocationId: LocationId,
                MaterialSpecId: ExistingMaterialSpecId.Value,
                ItemKind: ItemKind,
                Quantity: Quantity,
                UsableWidthIn: UsableWidthIn,
                UsableLengthIn: UsableLengthIn,
                ShapeClassification: ShapeClassification,
                IsQuarantined: IsQuarantined,
                VendorName: VendorName,
                PurchaseOrderNumber: PurchaseOrderNumber,
                ReceiptNumber: ReceiptNumber,
                VendorPartNumber: VendorPartNumber,
                HeatLotNumber: HeatLotNumber,
                CostPerUnit: CostPerUnit,
                Notes: Notes,
                PurchaseOrderLineId: PoLineId);

            var result = await receiveMaterial.ReceiveMaterialAsync(CorrelationId, request, userId);
            return RedirectToPage("Success", new { code = result.PublicItemCode });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or MaterialLedger.Data.Idempotency.OperationConflictException)
        {
            Error = ex is MaterialLedger.Data.Idempotency.OperationConflictException
                ? "This form was already submitted with different values — please review and resubmit."
                : ex.Message;
            return Page();
        }
    }

    private Task<List<MaterialSpec>> LoadAvailableSpecsAsync() => db.MaterialSpecs
        .AsNoTracking()
        .Where(x => x.IsActive)
        .OrderBy(x => x.MaterialFamily).ThenBy(x => x.Grade).ThenBy(x => x.Finish).ThenBy(x => x.ThicknessIn)
        .ToListAsync();
}
```

Replace the full contents of `MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml`:

```html
@* MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml *@
@page
@model MaterialLedger.Web.Pages.Receiving.NewReceiptModel
@{ Layout = "Shared/_Layout"; }
<h1>Receive material — @Model.Location?.LocationCode</h1>

@if (Model.PurchaseOrderLine is not null)
{
    <p>Receiving against PO <strong>@Model.PurchaseOrderLine.PurchaseOrder!.PurchaseOrderNumber</strong>, line @Model.PurchaseOrderLine.LineNumber —
    @(Model.PurchaseOrderLine.QuantityOrdered - Model.PurchaseOrderLine.QuantityReceived) remaining of @Model.PurchaseOrderLine.QuantityOrdered ordered.</p>
}

@if (Model.Error is not null)
{
    <p style="color:red">@Model.Error</p>
}

<form method="post">
    @Html.AntiForgeryToken()
    <input type="hidden" asp-for="LocationId" />
    <input type="hidden" asp-for="CorrelationId" />
    <input type="hidden" asp-for="PoLineId" />

    <fieldset>
        <legend>Material</legend>
        <label>Material family / grade / finish
            <select id="receipt-material" required>
                <option value="">Choose material</option>
                @foreach (var material in Model.AvailableSpecs.GroupBy(spec => $"{spec.MaterialFamily}|{spec.Grade}|{spec.Finish}"))
                {
                    var spec = material.First();
                    <option value="@material.Key">@spec.MaterialFamily@(spec.Grade is null ? "" : $" {spec.Grade}")@(spec.Finish is null ? "" : $" {spec.Finish}")</option>
                }
            </select>
        </label>
        <label>Thickness
            <select asp-for="ExistingMaterialSpecId" id="receipt-thickness" required disabled>
                <option value="">Choose thickness</option>
                @foreach (var spec in Model.AvailableSpecs)
                {
                    <option value="@spec.MaterialSpecId" data-material-key="@($"{spec.MaterialFamily}|{spec.Grade}|{spec.Finish}")">@spec.DisplayName</option>
                }
            </select>
        </label>
        <p>Need a material that is not listed? <a asp-page="/Materials/Index">Add it to the material catalog</a>.</p>
    </fieldset>

    <fieldset>
        <legend>Item</legend>
        <label><input type="radio" asp-for="ItemKind" value="FullSheetBundle" /> Full sheet / bundle</label>
        <label><input type="radio" asp-for="ItemKind" value="FullSheet" /> Single full sheet</label>
        <label>Quantity <input asp-for="Quantity" type="number" step="1" /></label>
        <label>Usable width (in) <input asp-for="UsableWidthIn" type="number" step="0.001" /></label>
        <label>Usable length (in) <input asp-for="UsableLengthIn" type="number" step="0.001" /></label>
        <label>Shape
            <select asp-for="ShapeClassification">
                <option value="Rectangular">Rectangular</option>
                <option value="IrregularConservativeRectangle">Irregular (conservative rectangle)</option>
                <option value="Unknown">Unknown</option>
            </select>
        </label>
    </fieldset>

    <fieldset>
        <legend>Receiving details</legend>
        <label>Vendor <input asp-for="VendorName" maxlength="200" /></label>
        <label>PO number (required) <input asp-for="PurchaseOrderNumber" maxlength="100" /></label>
        <label>Receipt number <input asp-for="ReceiptNumber" maxlength="100" /></label>
        <label>Vendor part number <input asp-for="VendorPartNumber" maxlength="100" /></label>
        <label>Heat/lot number <input asp-for="HeatLotNumber" maxlength="100" /></label>
        <label>Cost per unit <input asp-for="CostPerUnit" type="number" step="0.01" min="0" /></label>
    </fieldset>

    <label><input type="checkbox" asp-for="IsQuarantined" /> Damaged / questionable on arrival — quarantine (not available for reservation)</label>
    <label>Notes <textarea asp-for="Notes"></textarea></label>

    <button type="submit">Receive material</button>
</form>
<script>
    (() => {
        const material = document.getElementById('receipt-material');
        const thickness = document.getElementById('receipt-thickness');
        const selected = thickness.options[thickness.selectedIndex];
        if (selected?.dataset.materialKey) material.value = selected.dataset.materialKey;
        const filterThicknesses = reset => {
            for (const option of thickness.options) {
                if (!option.value) continue;
                const matches = option.dataset.materialKey === material.value;
                option.hidden = !matches;
                option.disabled = !matches;
            }
            thickness.disabled = !material.value;
            if (reset) thickness.value = '';
        };
        material.addEventListener('change', () => filterThicknesses(true));
        filterThicknesses(false);
    })();
</script>
```

- [ ] **Step 3: Build, then run the full Receiving test suite to confirm no regression**

Run: `dotnet build MaterialLedger.sln`
Expected: builds with no errors.

Run: `dotnet test MaterialLedger.Tests --filter "ReceiveMaterialServiceTests|ReceivingEntryPresentationTests"`
Expected: PASS (existing tests untouched by this page-only change).

- [ ] **Step 4: Commit**

```bash
git add MaterialLedger.Web/Pages/Receiving/SelectLocation.cshtml.cs MaterialLedger.Web/Pages/Receiving/SelectLocation.cshtml MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml.cs MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml
git commit -m "feat: pre-fill Receiving/NewReceipt from a purchase order line"
```

---

### Task 10: Navigation link

**Files:**
- Modify: `MaterialLedger.Web/Pages/Shared/_Layout.cshtml`
- Modify: `MaterialLedger.Tests/NavigationPresentationTests.cs`

**Interfaces:**
- Consumes: `PurchaseOrders/Index` page (Task 6).

- [ ] **Step 1: Write the failing assertions**

In `MaterialLedger.Tests/NavigationPresentationTests.cs`, in `Navigation_maps_each_visible_action_to_its_exact_page_route`, add after the `Locations/Index` assertion:

```csharp
        Assert.Contains("<a asp-page=\"/PurchaseOrders/Index\">Order material</a>", layout);
```

In `Navigation_renders_privileged_actions_only_inside_their_authorized_role_gates`, add the new link and its assertions:

```csharp
        const string orderMaterialLink = "<a asp-page=\"/PurchaseOrders/Index\">Order material</a>";
```

right after the existing `const string receivingLink = ...;` line, then add:

```csharp
        Assert.Equal(1, CountOccurrences(layout, orderMaterialLink));
        Assert.Contains(orderMaterialLink, programmerManagerGate);
```

right after the existing `Assert.Equal(1, CountOccurrences(layout, receivingLink));` line.

- [ ] **Step 2: Run to confirm it fails**

Run: `dotnet test MaterialLedger.Tests --filter NavigationPresentationTests`
Expected: FAIL — the link isn't in `_Layout.cshtml` yet.

- [ ] **Step 3: Add the nav link**

In `MaterialLedger.Web/Pages/Shared/_Layout.cshtml`, inside the `@if (User.IsInRole("Programmer") || User.IsInRole("Manager"))` block, add the new link as the first line inside the block (before `Receive delivery`):

```html
                    <a asp-page="/PurchaseOrders/Index">Order material</a>
```

So the block reads:

```html
                @if (User.IsInRole("Programmer") || User.IsInRole("Manager"))
                {
                    <a asp-page="/PurchaseOrders/Index">Order material</a>
                    <a asp-page="/Receiving/SelectLocation">Receive delivery</a>
                    <a asp-page="/Census/SelectSlot">Count existing material</a>
                    <a asp-page="/Materials/Index">Material catalog</a>
                }
```

- [ ] **Step 4: Run the tests to confirm they pass**

Run: `dotnet test MaterialLedger.Tests --filter NavigationPresentationTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/Shared/_Layout.cshtml MaterialLedger.Tests/NavigationPresentationTests.cs
git commit -m "feat: add Order material nav link"
```

---

### Task 11: Full solution verification

**Files:** none (verification only).

- [ ] **Step 1: Full build**

Run: `dotnet build MaterialLedger.sln`
Expected: builds with no errors or warnings introduced by this feature.

- [ ] **Step 2: Full test suite**

Run: `dotnet test MaterialLedger.sln`
Expected: PASS, all tests green (requires `MATERIALLEDGER_TEST_ADMIN_CONNECTION` set for the `Integration`-traited tests, per `SqlServerTestDatabaseFixture`).

- [ ] **Step 3: Record follow-up in `docs/PROGRESS.md` if the repo tracks one, otherwise note verbally**

No code change — this step is a reminder for whoever runs the plan: once this ships and the migration (`AddPurchaseOrders`) and `docs/schema/material-permissions-v8.sql` are applied to the real Forge SQL Server, the next step is to actually enter the O'Neal Steel PO 46318 (order 22930552, 7 lines — A36 plate, 3/16" and 3/8", various widths/lengths) through the new `PurchaseOrders/Create` page, which is the original task that prompted this feature.
