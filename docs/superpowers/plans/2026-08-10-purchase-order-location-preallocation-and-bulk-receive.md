# Purchase Order Location Preallocation & Bulk Receive Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a Programmer/Manager plan, ahead of a delivery's arrival, which shelves a PO line's quantity will go to (including splitting one line across several shelves), then receive an entire PO in one pass from `PurchaseOrders/Details` instead of walking each line through `Receiving/SelectLocation` → `Receiving/NewReceipt` individually.

**Architecture:** One new EF entity, `PurchaseOrderLineAllocation` (a location + planned quantity attached to a `PurchaseOrderLine`), with two new `PurchaseOrderService` methods to add/remove them. Bulk receiving is **not** a new service method — it's a loop in a new `PurchaseOrders/ReceiveAll` page handler over the existing `ReceiveMaterialService.ReceiveMaterialAsync`, one already-atomic call per checked row. `PurchaseOrders/Details` gains an inline allocation planner per line and a "Receive all" link.

**Tech Stack:** ASP.NET Core Razor Pages (.NET 8), EF Core 9 / SQL Server, xUnit integration tests against a real throwaway SQL Server database.

## Global Constraints

- `AddLocationAllocationAsync`/`RemoveLocationAllocationAsync` follow the exact transactional/idempotency shape used throughout this codebase: SHA-256 payload hash → `BeginTransactionAsync(IsolationLevel.Serializable)` → `OperationLogGuard.CheckAsync`/replay → business writes → `OperationLogGuard.Record` (not `RecordAsync`) → final `SaveChangesAsync()` → `CommitAsync()`.
- New/modified pages are gated `[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]`.
- Forms use `method="post"` + a single raw `@Html.AntiForgeryToken()` and **never** mix `asp-page`/`asp-route-*` tag helpers with an explicit antiforgery token on the same `<form>`. Plain `<a>` navigation links may freely use `asp-page`/`asp-route-*`.
- `QuantityPlanned` is `decimal(12,3)`, matching every other quantity column in this schema.
- **No changes to `ReceiveMaterialService`.** A planned allocation is just a location + quantity that pre-fills an ordinary `ReceiveMaterialAsync` call; `ReceiveAll` is page-handler orchestration only.
- **No enforcement that a line's allocations sum to its full ordered quantity.** Partial planning is allowed.
- Leaf-location rule (`Shelf`, `Bay`, `Staging`, `Quarantine`, `Scrap`) is re-checked independently inside `AddLocationAllocationAsync` — this table has no FK-level constraint that could express it, same rule `ReceiveMaterialService` already enforces.
- New schema/permission files are additive only: next EF migration, and `docs/schema/material-permissions-v9.sql` — **never edit v1–v8**. Permission files are applied manually against the real Forge SQL Server by a human.
- Match established file-organization convention: `PurchaseOrderLineAllocation` lives in the same file as `PurchaseOrder`/`PurchaseOrderLine` (`MaterialLedger.Data/Models/PurchaseOrder.cs`).

---

### Task 1: Data model, EF migration, permissions grant

**Files:**
- Modify: `MaterialLedger.Data/Models/PurchaseOrder.cs`
- Modify: `MaterialLedger.Data/MaterialLedgerDbContext.cs`
- Create: EF migration (generated file under `MaterialLedger.Data/Migrations/`)
- Create: `docs/schema/material-permissions-v9.sql`
- Modify: `MaterialLedger.Tests/SchemaShapeTests.cs`

**Interfaces:**
- Produces: `PurchaseOrderLineAllocation` in `MaterialLedger.Data.Models`, `PurchaseOrderLine.LocationAllocations` (`List<PurchaseOrderLineAllocation>`), `MaterialLedgerDbContext.PurchaseOrderLineAllocations` (`DbSet<T>`) — used by Task 2, Task 3, Task 4.

- [ ] **Step 1: Update the schema-shape test to expect the new table count (red)**

In `MaterialLedger.Tests/SchemaShapeTests.cs`, rename the test and bump the count:

```csharp
    [Fact]
    public async Task AllSixteenMaterialTables_Exist()
    {
        await using var db = _fixture.CreateContext();
        await using var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'material'";
        var count = (int)(await command.ExecuteScalarAsync())!;
        Assert.Equal(16, count);
    }
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test MaterialLedger.Tests --filter AllSixteenMaterialTables_Exist`
Expected: FAIL — actual count is 15.

- [ ] **Step 3: Add the `PurchaseOrderLineAllocation` model**

In `MaterialLedger.Data/Models/PurchaseOrder.cs`, add `LocationAllocations` to `PurchaseOrderLine` and append the new class at the end of the file:

```csharp
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
    public List<PurchaseOrderLineAllocation> LocationAllocations { get; set; } = [];
}

public class PurchaseOrderLineAllocation
{
    public long PurchaseOrderLineAllocationId { get; set; }
    public long PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    public long InventoryLocationId { get; set; }
    public InventoryLocation? InventoryLocation { get; set; }
    public decimal QuantityPlanned { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedBy { get; set; }
}
```

- [ ] **Step 4: Register the DbSet and entity configuration**

In `MaterialLedger.Data/MaterialLedgerDbContext.cs`, add a DbSet property right after `public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();`:

```csharp
    public DbSet<PurchaseOrderLineAllocation> PurchaseOrderLineAllocations => Set<PurchaseOrderLineAllocation>();
```

Add a new entity configuration block immediately after the existing `modelBuilder.Entity<PurchaseOrderLine>(e => { ... });` block (before `modelBuilder.Entity<InventoryItem>`):

```csharp
        modelBuilder.Entity<PurchaseOrderLineAllocation>(e =>
        {
            e.HasKey(x => x.PurchaseOrderLineAllocationId);
            e.Property(x => x.QuantityPlanned).HasColumnType("decimal(12,3)").IsRequired();
            e.ToTable(t => t.HasCheckConstraint("CK_PurchaseOrderLineAllocation_QuantityPlanned",
                "[QuantityPlanned] = FLOOR([QuantityPlanned]) AND [QuantityPlanned] >= 1"));
            e.HasOne(x => x.PurchaseOrderLine).WithMany(x => x.LocationAllocations)
                .HasForeignKey(x => x.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.InventoryLocation).WithMany()
                .HasForeignKey(x => x.InventoryLocationId).OnDelete(DeleteBehavior.Restrict);
        });
```

- [ ] **Step 5: Generate the EF migration**

Run: `dotnet ef migrations add AddPurchaseOrderLineAllocations --project MaterialLedger.Data --startup-project MaterialLedger.Web`

Open the generated file under `MaterialLedger.Data/Migrations/` and confirm it creates `material.PurchaseOrderLineAllocations` with the check constraint above and `Restrict` FKs to both `PurchaseOrderLines` and `InventoryLocations`, and that `MaterialLedgerDbContextModelSnapshot.cs` was updated alongside it.

- [ ] **Step 6: Run the schema-shape test to confirm it now passes**

Run: `dotnet test MaterialLedger.Tests --filter AllSixteenMaterialTables_Exist`
Expected: PASS (count is now 16). `SqlServerTestDatabaseFixture` re-provisions the test database and calls `Database.MigrateAsync()` per test class run, so no manual `dotnet ef database update` is needed for the test DB.

- [ ] **Step 7: Add the permissions grant file**

Create `docs/schema/material-permissions-v9.sql`:

```sql
-- docs/schema/material-permissions-v9.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, purchase order
-- location preallocation and bulk receive.
--
-- Grants the reads/inserts/deletes PurchaseOrderService's AddLocationAllocationAsync and
-- RemoveLocationAllocationAsync need. DELETE is new here — no prior grant file has needed it,
-- since every other write path in this app is insert/update-only (cancel-by-status-flip, not
-- delete). Allocations are the first genuinely deletable row in this schema.
--
-- Run against the real `MaterialLedger` database only after the AddPurchaseOrderLineAllocations
-- EF migration has been applied, using an account with GRANT rights. This is a manual
-- administrative step; the application never applies its own schema or permission changes.

USE MaterialLedger;
GO

GRANT SELECT, INSERT, DELETE ON material.PurchaseOrderLineAllocations TO materialledger_app;
GO
```

- [ ] **Step 8: Full build check, then commit**

Run: `dotnet build MaterialLedger.sln`
Expected: builds with no errors.

```bash
git add MaterialLedger.Data/Models/PurchaseOrder.cs MaterialLedger.Data/MaterialLedgerDbContext.cs MaterialLedger.Data/Migrations/ docs/schema/material-permissions-v9.sql MaterialLedger.Tests/SchemaShapeTests.cs
git commit -m "feat: add PurchaseOrderLineAllocation schema"
```

---

### Task 2: `PurchaseOrderService.AddLocationAllocationAsync` / `RemoveLocationAllocationAsync`

**Files:**
- Modify: `MaterialLedger.Data/PurchaseOrders/PurchaseOrderService.cs`
- Test: Modify `MaterialLedger.Tests/PurchaseOrderServiceTests.cs`

**Interfaces:**
- Consumes: `PurchaseOrderLineAllocation` (Task 1).
- Produces: `AddLocationAllocationRequest(long PurchaseOrderLineId, long InventoryLocationId, decimal Quantity)`, `PurchaseOrderService.AddLocationAllocationAsync(Guid correlationId, AddLocationAllocationRequest request, int createdBy) : Task<long>`, `PurchaseOrderService.RemoveLocationAllocationAsync(Guid correlationId, long allocationId, int removedBy) : Task` — both used by Task 3 and Task 5.

- [ ] **Step 1: Write the failing tests**

Append to `MaterialLedger.Tests/PurchaseOrderServiceTests.cs` (inside the class, before the closing brace):

```csharp
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
```

- [ ] **Step 2: Run to confirm it fails**

Run: `dotnet test MaterialLedger.Tests --filter PurchaseOrderServiceTests`
Expected: FAIL to compile — `AddLocationAllocationAsync`/`RemoveLocationAllocationAsync`/`AddLocationAllocationRequest` don't exist yet.

- [ ] **Step 3: Implement both methods**

In `MaterialLedger.Data/PurchaseOrders/PurchaseOrderService.cs`, add the request record right after `CreatePurchaseOrderRequest` (or wherever the existing request records live), and add the two methods to `PurchaseOrderService` after `CloseRemainingAsync` (before `CancelLineCore`):

```csharp
public record AddLocationAllocationRequest(long PurchaseOrderLineId, long InventoryLocationId, decimal Quantity);
```

```csharp
    private static readonly InventoryLocationType[] LeafLocationTypes =
    [
        InventoryLocationType.Shelf, InventoryLocationType.Bay, InventoryLocationType.Staging,
        InventoryLocationType.Quarantine, InventoryLocationType.Scrap
    ];

    public async Task<long> AddLocationAllocationAsync(Guid correlationId, AddLocationAllocationRequest request, int createdBy)
    {
        ValidateAllocation(request);

        var payloadHash = HashPayload(request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "AddPurchaseOrderLineAllocation", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replayId = JsonSerializer.Deserialize<long>(outcome.ResultSummary!);
            await transaction.CommitAsync();
            return replayId;
        }

        var line = await db.PurchaseOrderLines
            .Include(x => x.LocationAllocations)
            .FirstOrDefaultAsync(x => x.PurchaseOrderLineId == request.PurchaseOrderLineId)
            ?? throw new InvalidOperationException($"Purchase order line {request.PurchaseOrderLineId} does not exist.");
        if (line.Status is PurchaseOrderLineStatus.Received or PurchaseOrderLineStatus.Cancelled)
            throw new InvalidOperationException(
                $"Purchase order line {line.PurchaseOrderLineId} is {line.Status} and cannot have a delivery location planned for it.");

        var location = await db.InventoryLocations.FindAsync(request.InventoryLocationId)
            ?? throw new InvalidOperationException($"Location {request.InventoryLocationId} does not exist.");
        if (!LeafLocationTypes.Contains(location.LocationType))
            throw new InvalidOperationException(
                $"Location '{location.LocationCode}' is a {location.LocationType} — allocations can only target a leaf location.");

        var alreadyAllocated = line.LocationAllocations.Sum(x => x.QuantityPlanned);
        var remaining = line.QuantityOrdered - line.QuantityReceived;
        if (alreadyAllocated + request.Quantity > remaining)
            throw new InvalidOperationException(
                $"Cannot allocate {request.Quantity} — only {remaining - alreadyAllocated} of purchase order line {line.PurchaseOrderLineId} remains unallocated.");

        var allocation = new PurchaseOrderLineAllocation
        {
            PurchaseOrderLineId = line.PurchaseOrderLineId,
            InventoryLocationId = request.InventoryLocationId,
            QuantityPlanned = request.Quantity,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = createdBy
        };
        db.PurchaseOrderLineAllocations.Add(allocation);
        await db.SaveChangesAsync();

        operationLog.Record(correlationId, "AddPurchaseOrderLineAllocation", payloadHash, JsonSerializer.Serialize(allocation.PurchaseOrderLineAllocationId), createdBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return allocation.PurchaseOrderLineAllocationId;
    }

    public async Task RemoveLocationAllocationAsync(Guid correlationId, long allocationId, int removedBy)
    {
        var payloadHash = HashPayload(new { allocationId });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "RemovePurchaseOrderLineAllocation", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            await transaction.CommitAsync();
            return;
        }

        var allocation = await db.PurchaseOrderLineAllocations.FindAsync(allocationId);
        if (allocation is not null)
            db.PurchaseOrderLineAllocations.Remove(allocation);
        await db.SaveChangesAsync();

        operationLog.Record(correlationId, "RemovePurchaseOrderLineAllocation", payloadHash, JsonSerializer.Serialize(true), removedBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static void ValidateAllocation(AddLocationAllocationRequest request)
    {
        if (request.Quantity < 1 || request.Quantity != decimal.Truncate(request.Quantity))
            throw new ArgumentException("Allocation quantity must be a whole number of at least 1.", nameof(request));
    }
```

- [ ] **Step 4: Run the tests to confirm they pass**

Run: `dotnet test MaterialLedger.Tests --filter PurchaseOrderServiceTests`
Expected: PASS (22 tests total — 10 existing + 12 new).

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Data/PurchaseOrders/PurchaseOrderService.cs MaterialLedger.Tests/PurchaseOrderServiceTests.cs
git commit -m "feat: add PurchaseOrderService location allocation add/remove"
```

---

### Task 3: `PurchaseOrders/Details` — allocation planner and "Receive all" link

**Files:**
- Modify: `MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml`

**Interfaces:**
- Consumes: `PurchaseOrderService.AddLocationAllocationAsync`/`RemoveLocationAllocationAsync` (Task 2), `ActiveMaterialSlotQuery.Create` (existing, `MaterialLedger.Web.Pages.Shared`).
- Produces: the `/PurchaseOrders/ReceiveAll?id={poId}` link consumed by Task 4/5.

- [ ] **Step 1: Add the handlers and `LeafLocations`/allocation data to `DetailsModel`**

Replace the full contents of `MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml.cs`:

```csharp
// MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Attachments;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.PurchaseOrders;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Pages.Shared;
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
    public List<InventoryLocation> LeafLocations { get; set; } = [];
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
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await purchaseOrderService.CloseRemainingAsync(CorrelationId, Id, userId);
            return RedirectToPage(new { id = Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationConflictException)
        {
            if (!await LoadAsync()) return NotFound();
            Error = ex.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostAddAllocationAsync(long lineId, long inventoryLocationId, decimal quantity)
    {
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await purchaseOrderService.AddLocationAllocationAsync(
                CorrelationId, new AddLocationAllocationRequest(lineId, inventoryLocationId, quantity), userId);
            return RedirectToPage(new { id = Id });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationConflictException)
        {
            if (!await LoadAsync()) return NotFound();
            Error = ex.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRemoveAllocationAsync(long allocationId)
    {
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await purchaseOrderService.RemoveLocationAllocationAsync(CorrelationId, allocationId, userId);
            return RedirectToPage(new { id = Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationConflictException)
        {
            if (!await LoadAsync()) return NotFound();
            Error = ex.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnGetDownloadAttachmentAsync(long attachmentId)
    {
        if (!await LoadAsync()) return NotFound();

        var attachment = Attachments.FirstOrDefault(x => x.AttachmentId == attachmentId);
        if (attachment is null) return NotFound();

        var path = attachmentStorage.GetFullPath(attachment);
        if (!System.IO.File.Exists(path)) return NotFound();

        var downloadFileName = $"{Order!.PurchaseOrderNumber}-confirmation{Path.GetExtension(attachment.StoragePath)}";
        return PhysicalFile(path, attachment.ContentType ?? "application/octet-stream", downloadFileName);
    }

    private async Task<bool> LoadAsync()
    {
        Order = await db.PurchaseOrders
            .Include(x => x.Lines).ThenInclude(x => x.MaterialSpec)
            .Include(x => x.Lines).ThenInclude(x => x.LocationAllocations).ThenInclude(x => x.InventoryLocation)
            .FirstOrDefaultAsync(x => x.PurchaseOrderId == Id);
        if (Order is null) return false;

        LeafLocations = await ActiveMaterialSlotQuery.Create(db).ToListAsync();

        Attachments = await db.Attachments
            .Where(x => x.EntityType == AttachmentEntityType.PurchaseOrder && x.EntityId == Id)
            .OrderByDescending(x => x.UploadedAtUtc)
            .ToListAsync();
        return true;
    }
}
```

- [ ] **Step 2: Add the allocation planner and "Receive all" link to the view**

Replace the full contents of `MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml`:

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
            <td>@PurchaseOrderStatusDisplay.Label(line.Status)</td>
            <td>
                @if (line.Status is PurchaseOrderLineStatus.Open or PurchaseOrderLineStatus.PartiallyReceived)
                {
                    <a asp-page="/Receiving/SelectLocation" asp-route-poLineId="@line.PurchaseOrderLineId">Receive</a>
                    <form action="/PurchaseOrders/Details/@Model.Order.PurchaseOrderId?handler=CancelLine&amp;lineId=@line.PurchaseOrderLineId" method="post">
                        @Html.AntiForgeryToken()
                        <input type="hidden" asp-for="CorrelationId" />
                        <button type="submit">Cancel line</button>
                    </form>
                }
            </td>
        </tr>
        @if (line.Status is PurchaseOrderLineStatus.Open or PurchaseOrderLineStatus.PartiallyReceived)
        {
            var lineRemaining = line.QuantityOrdered - line.QuantityReceived;
            var lineAllocated = line.LocationAllocations.Sum(x => x.QuantityPlanned);
            var lineUnallocated = lineRemaining - lineAllocated;
            <tr>
                <td colspan="9">
                    <strong>Planned delivery locations</strong>
                    @if (line.LocationAllocations.Count == 0)
                    {
                        <p>None planned yet.</p>
                    }
                    else
                    {
                        <ul>
                        @foreach (var allocation in line.LocationAllocations.OrderBy(x => x.PurchaseOrderLineAllocationId))
                        {
                            <li>
                                @allocation.InventoryLocation?.LocationCode — @DisplayFormat.Quantity(allocation.QuantityPlanned)
                                <form action="/PurchaseOrders/Details/@Model.Order.PurchaseOrderId?handler=RemoveAllocation&amp;allocationId=@allocation.PurchaseOrderLineAllocationId" method="post" style="display:inline">
                                    @Html.AntiForgeryToken()
                                    <input type="hidden" asp-for="CorrelationId" />
                                    <button type="submit">Remove</button>
                                </form>
                            </li>
                        }
                        </ul>
                    }
                    <p>Unallocated: @DisplayFormat.Quantity(lineUnallocated)</p>
                    <form action="/PurchaseOrders/Details/@Model.Order.PurchaseOrderId?handler=AddAllocation" method="post">
                        @Html.AntiForgeryToken()
                        <input type="hidden" asp-for="CorrelationId" />
                        <input type="hidden" name="lineId" value="@line.PurchaseOrderLineId" />
                        <label>Location
                            <select name="inventoryLocationId" required>
                                <option value="">Choose location</option>
                                @foreach (var loc in Model.LeafLocations)
                                {
                                    <option value="@loc.InventoryLocationId">@loc.LocationCode</option>
                                }
                            </select>
                        </label>
                        <label>Quantity <input name="quantity" type="number" step="1" min="1" /></label>
                        <button type="submit">Add allocation</button>
                    </form>
                </td>
            </tr>
        }
    }
    </tbody>
</table>

@if (status == PurchaseOrderStatus.Open)
{
    <p><a asp-page="ReceiveAll" asp-route-id="@Model.Order.PurchaseOrderId">Receive all</a></p>
    <form action="/PurchaseOrders/Details/@Model.Order.PurchaseOrderId?handler=CloseRemaining" method="post">
        @Html.AntiForgeryToken()
        <input type="hidden" asp-for="CorrelationId" />
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

- [ ] **Step 4: Manual verification**

Run the app (`dotnet run --project MaterialLedger.Web`), open a PO with an open line, and confirm: the "Planned delivery locations" panel renders under each open/partially-received line, adding an allocation shows it in the list and reduces the "Unallocated" hint, removing it restores the hint, and attempting to allocate more than remains shows the `Error` message instead of a 500.

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml.cs MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml
git commit -m "feat: add location allocation planner and Receive all link to PurchaseOrders/Details"
```

---

### Task 4: `PurchaseOrders/ReceiveAll` — build the row list (`OnGetAsync`)

**Files:**
- Create: `MaterialLedger.Web/Pages/PurchaseOrders/ReceiveAll.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/PurchaseOrders/ReceiveAll.cshtml`

**Interfaces:**
- Consumes: `PurchaseOrder.Lines`, `PurchaseOrderLine.LocationAllocations` (Task 1), `ActiveMaterialSlotQuery.Create` (existing).
- Produces: `ReceiveAllRow` (row model — see fields below), `ReceiveAllModel.Rows` (`List<ReceiveAllRow>`), `ReceiveAllModel.Order`, `ReceiveAllModel.LeafLocations` — consumed by Task 5.

- [ ] **Step 1: Create the page model with `OnGetAsync`**

Create `MaterialLedger.Web/Pages/PurchaseOrders/ReceiveAll.cshtml.cs`:

```csharp
// MaterialLedger.Web/Pages/PurchaseOrders/ReceiveAll.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.PurchaseOrders;
using MaterialLedger.Data.Receiving;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.PurchaseOrders;

public class ReceiveAllRow
{
    public Guid CorrelationId { get; set; }
    public long? AllocationId { get; set; }
    public long PurchaseOrderLineId { get; set; }
    public int LineNumber { get; set; }
    public string? MaterialDisplayName { get; set; }
    public bool Selected { get; set; }
    public long? LocationId { get; set; }
    public string? LocationDisplay { get; set; }
    public decimal Quantity { get; set; }
    public string? HeatLotNumber { get; set; }
    public decimal? CostPerUnit { get; set; }
    public bool IsQuarantined { get; set; }
    public string? Notes { get; set; }
    public string? SucceededItemCode { get; set; }
    public string? Error { get; set; }
    public string? Warning { get; set; }
}

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class ReceiveAllModel(MaterialLedgerDbContext db, ReceiveMaterialService receiveMaterial, PurchaseOrderService purchaseOrderService) : PageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty] public List<ReceiveAllRow> Rows { get; set; } = [];

    public PurchaseOrder? Order { get; set; }
    public List<InventoryLocation> LeafLocations { get; set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        Order = await LoadOrderAsync();
        if (Order is null) return NotFound();

        LeafLocations = await ActiveMaterialSlotQuery.Create(db).ToListAsync();
        Rows = BuildRows(Order);
        return Page();
    }

    private Task<PurchaseOrder?> LoadOrderAsync() => db.PurchaseOrders
        .Include(x => x.Lines).ThenInclude(x => x.MaterialSpec)
        .Include(x => x.Lines).ThenInclude(x => x.LocationAllocations).ThenInclude(x => x.InventoryLocation)
        .FirstOrDefaultAsync(x => x.PurchaseOrderId == Id);

    private static List<ReceiveAllRow> BuildRows(PurchaseOrder order)
    {
        var rows = new List<ReceiveAllRow>();
        foreach (var line in order.Lines
            .Where(x => x.Status is PurchaseOrderLineStatus.Open or PurchaseOrderLineStatus.PartiallyReceived)
            .OrderBy(x => x.LineNumber))
        {
            foreach (var allocation in line.LocationAllocations.OrderBy(x => x.PurchaseOrderLineAllocationId))
            {
                rows.Add(new ReceiveAllRow
                {
                    CorrelationId = Guid.NewGuid(),
                    AllocationId = allocation.PurchaseOrderLineAllocationId,
                    PurchaseOrderLineId = line.PurchaseOrderLineId,
                    LineNumber = line.LineNumber,
                    MaterialDisplayName = line.MaterialSpec?.DisplayName,
                    Selected = true,
                    LocationId = allocation.InventoryLocationId,
                    LocationDisplay = allocation.InventoryLocation?.LocationCode,
                    Quantity = allocation.QuantityPlanned,
                    CostPerUnit = line.UnitPrice
                });
            }

            var allocated = line.LocationAllocations.Sum(x => x.QuantityPlanned);
            var unallocated = line.QuantityOrdered - line.QuantityReceived - allocated;
            if (unallocated > 0)
            {
                rows.Add(new ReceiveAllRow
                {
                    CorrelationId = Guid.NewGuid(),
                    AllocationId = null,
                    PurchaseOrderLineId = line.PurchaseOrderLineId,
                    LineNumber = line.LineNumber,
                    MaterialDisplayName = line.MaterialSpec?.DisplayName,
                    Selected = false,
                    LocationId = null,
                    Quantity = unallocated,
                    CostPerUnit = line.UnitPrice
                });
            }
        }
        return rows;
    }
}
```

- [ ] **Step 2: Create the view**

Create `MaterialLedger.Web/Pages/PurchaseOrders/ReceiveAll.cshtml`:

```html
@* MaterialLedger.Web/Pages/PurchaseOrders/ReceiveAll.cshtml *@
@page "{id:long}"
@model MaterialLedger.Web.Pages.PurchaseOrders.ReceiveAllModel
@using MaterialLedger.Web.Shared
@{ Layout = "Shared/_Layout"; }
<header class="page-heading">
    <p class="eyebrow">Purchase orders</p>
    <h1>Receive all — @Model.Order!.PurchaseOrderNumber — @Model.Order.VendorName</h1>
</header>

@if (Model.Rows.Count == 0)
{
    <p>No open lines with quantity remaining.</p>
}
else
{
    <form method="post">
        @Html.AntiForgeryToken()
        <table>
            <thead>
                <tr>
                    <th></th><th>Line</th><th>Material</th><th>Location</th><th>Quantity</th>
                    <th>Heat/lot</th><th>Cost/unit</th><th>Quarantine</th><th>Notes</th><th>Result</th>
                </tr>
            </thead>
            <tbody>
            @for (var i = 0; i < Model.Rows.Count; i++)
            {
                var row = Model.Rows[i];
                <tr>
                    <td>
                        <input type="hidden" asp-for="Rows[i].CorrelationId" />
                        <input type="hidden" asp-for="Rows[i].AllocationId" />
                        <input type="hidden" asp-for="Rows[i].PurchaseOrderLineId" />
                        <input type="hidden" asp-for="Rows[i].LineNumber" />
                        <input type="hidden" asp-for="Rows[i].MaterialDisplayName" />
                        @if (row.SucceededItemCode is not null)
                        {
                            <input type="hidden" asp-for="Rows[i].Selected" />
                            <input type="hidden" asp-for="Rows[i].SucceededItemCode" />
                        }
                        else
                        {
                            <input asp-for="Rows[i].Selected" type="checkbox" />
                        }
                    </td>
                    <td>@row.LineNumber</td>
                    <td>@row.MaterialDisplayName</td>
                    @if (row.SucceededItemCode is not null)
                    {
                        <td>@row.LocationDisplay<input type="hidden" asp-for="Rows[i].LocationId" /></td>
                        <td>@DisplayFormat.Quantity(row.Quantity)<input type="hidden" asp-for="Rows[i].Quantity" /></td>
                        <td>@row.HeatLotNumber<input type="hidden" asp-for="Rows[i].HeatLotNumber" /></td>
                        <td>@(row.CostPerUnit?.ToString("0.00") ?? "—")<input type="hidden" asp-for="Rows[i].CostPerUnit" /></td>
                        <td>@(row.IsQuarantined ? "Yes" : "No")<input type="hidden" asp-for="Rows[i].IsQuarantined" /></td>
                        <td>@row.Notes<input type="hidden" asp-for="Rows[i].Notes" /></td>
                        <td>Received as @row.SucceededItemCode</td>
                    }
                    else
                    {
                        <td>
                            <select asp-for="Rows[i].LocationId">
                                <option value="">Choose location</option>
                                @foreach (var loc in Model.LeafLocations)
                                {
                                    <option value="@loc.InventoryLocationId">@loc.LocationCode</option>
                                }
                            </select>
                        </td>
                        <td><input asp-for="Rows[i].Quantity" type="number" step="1" min="1" /></td>
                        <td><input asp-for="Rows[i].HeatLotNumber" maxlength="100" /></td>
                        <td><input asp-for="Rows[i].CostPerUnit" type="number" step="0.01" min="0" /></td>
                        <td><input asp-for="Rows[i].IsQuarantined" type="checkbox" /></td>
                        <td><input asp-for="Rows[i].Notes" /></td>
                        <td style="color:red">
                            @row.Error
                            @if (row.Warning is not null)
                            {
                                <text> @row.Warning</text>
                            }
                        </td>
                    }
                </tr>
            }
            </tbody>
        </table>
        <button type="submit">Receive checked rows</button>
    </form>
}

<p><a asp-page="Details" asp-route-id="@Model.Order.PurchaseOrderId">Back to purchase order</a></p>
```

- [ ] **Step 3: Build and manually verify the page loads**

Run: `dotnet build MaterialLedger.sln`
Expected: builds with no errors.

Run the app and open `/PurchaseOrders/ReceiveAll/{id}` for a PO with an allocation and a line with unallocated remainder. Confirm: one row per allocation (checked, location/quantity prefilled) plus one row per line's unallocated remainder (unchecked, location blank, quantity prefilled with the remainder).

- [ ] **Step 4: Commit**

```bash
git add MaterialLedger.Web/Pages/PurchaseOrders/ReceiveAll.cshtml.cs MaterialLedger.Web/Pages/PurchaseOrders/ReceiveAll.cshtml
git commit -m "feat: add PurchaseOrders/ReceiveAll row builder and view"
```

---

### Task 5: `PurchaseOrders/ReceiveAll` — bulk receive loop (`OnPostAsync`)

**Files:**
- Modify: `MaterialLedger.Web/Pages/PurchaseOrders/ReceiveAll.cshtml.cs`

**Interfaces:**
- Consumes: `ReceiveMaterialService.ReceiveMaterialAsync` (existing), `PurchaseOrderService.RemoveLocationAllocationAsync` (Task 2), `ReceiveAllRow` (Task 4).

- [ ] **Step 1: Add `OnPostAsync`**

In `MaterialLedger.Web/Pages/PurchaseOrders/ReceiveAll.cshtml.cs`, add after `OnGetAsync`:

```csharp
    public async Task<IActionResult> OnPostAsync()
    {
        Order = await LoadOrderAsync();
        if (Order is null) return NotFound();
        LeafLocations = await ActiveMaterialSlotQuery.Create(db).ToListAsync();

        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        foreach (var row in Rows)
        {
            row.Error = null;
            row.Warning = null;
            if (!row.Selected) continue;

            if (row.LocationId is null)
            {
                row.Error = "Choose a location.";
                continue;
            }
            if (row.Quantity < 1 || row.Quantity != decimal.Truncate(row.Quantity))
            {
                row.Error = "Quantity must be a whole number of at least 1.";
                continue;
            }

            var line = Order.Lines.FirstOrDefault(x => x.PurchaseOrderLineId == row.PurchaseOrderLineId);
            if (line is null)
            {
                row.Error = "This line no longer exists.";
                continue;
            }

            try
            {
                var request = new ReceiveMaterialRequest(
                    InventoryLocationId: row.LocationId.Value,
                    MaterialSpecId: line.MaterialSpecId,
                    ItemKind: InventoryItemKind.FullSheetBundle,
                    Quantity: row.Quantity,
                    UsableWidthIn: line.UsableWidthIn,
                    UsableLengthIn: line.UsableLengthIn,
                    ShapeClassification: ShapeClassification.Rectangular,
                    IsQuarantined: row.IsQuarantined,
                    VendorName: Order.VendorName,
                    PurchaseOrderNumber: Order.PurchaseOrderNumber,
                    ReceiptNumber: null,
                    VendorPartNumber: line.VendorPartNumber,
                    HeatLotNumber: row.HeatLotNumber,
                    CostPerUnit: row.CostPerUnit,
                    Notes: row.Notes,
                    PurchaseOrderLineId: line.PurchaseOrderLineId);

                var result = await receiveMaterial.ReceiveMaterialAsync(row.CorrelationId, request, userId);
                row.SucceededItemCode = result.PublicItemCode;
                row.LocationDisplay = LeafLocations.FirstOrDefault(x => x.InventoryLocationId == row.LocationId)?.LocationCode;

                if (row.AllocationId is not null)
                {
                    try
                    {
                        await purchaseOrderService.RemoveLocationAllocationAsync(Guid.NewGuid(), row.AllocationId.Value, userId);
                    }
                    catch (Exception)
                    {
                        row.Warning = "Received, but its plan entry couldn't be cleared — remove it manually.";
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationConflictException)
            {
                row.Error = ex is OperationConflictException
                    ? "This row was already submitted with different values — please review and resubmit."
                    : ex.Message;
            }
        }

        return Page();
    }
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build MaterialLedger.sln`
Expected: builds with no errors.

- [ ] **Step 3: Manual verification (golden path, partial failure, resubmit)**

Run the app and, on a PO with two allocated rows targeting different locations plus one unallocated-remainder row:

1. Check all rows and submit. Confirm every row succeeds, shows its new item code read-only, and each succeeded allocation-backed row's plan entry is gone from `PurchaseOrders/Details`.
2. Repeat against a fresh PO, but first make one target row's location invalid (e.g. a non-leaf location id via a modified request, or exhaust its remaining quantity by receiving it elsewhere first) so it fails validation inside `ReceiveMaterialAsync`. Confirm the other rows still succeed, the failed row shows its `Error` message and stays checked/editable, and unchecked rows stay editable.
3. Fix the failed row's location/quantity and resubmit the same page. Confirm only the previously-failed row writes a new item — the already-succeeded rows replay as no-ops (same item codes, no duplicate `InventoryItem`/`SheetLot` rows, no duplicate receipt).

- [ ] **Step 4: Commit**

```bash
git add MaterialLedger.Web/Pages/PurchaseOrders/ReceiveAll.cshtml.cs
git commit -m "feat: add PurchaseOrders/ReceiveAll bulk receive loop"
```

---

### Task 6: Full solution verification

**Files:** none (verification only).

- [ ] **Step 1: Full build**

Run: `dotnet build MaterialLedger.sln`
Expected: builds with no errors or warnings introduced by this feature.

- [ ] **Step 2: Full test suite**

Run: `dotnet test MaterialLedger.sln`
Expected: PASS, all tests green (requires `MATERIALLEDGER_TEST_ADMIN_CONNECTION` set for the `Integration`-traited tests, per `SqlServerTestDatabaseFixture`).

- [ ] **Step 3: Manual/Playwright pass**

No code change — final QA checklist per the design's Testing section, ideally driven through the `webapp-testing` skill against a running dev server:

- On `Details`: add an allocation, confirm it appears and the remaining-quantity hint drops by the allocated amount; remove it and confirm the hint returns; attempt to over-allocate and confirm the page shows an error instead of a 500.
- On `ReceiveAll`: golden path receives every checked row and clears the allocations that backed them; a mid-batch failure (two rows, one targeting a location/quantity that fails validation) leaves the succeeded row read-only and the failed row editable; resubmitting after fixing the failed row doesn't duplicate the already-succeeded ones.

- [ ] **Step 4: Record follow-up**

No code change — once this ships, the migration (`AddPurchaseOrderLineAllocations`) and `docs/schema/material-permissions-v9.sql` need to be applied to the real Forge SQL Server before this feature can be used against the live O'Neal Steel PO with the 28-piece line that motivated this design.
