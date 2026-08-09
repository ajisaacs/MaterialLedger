# Receive Material Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the `ReceiveMaterial` write procedure and UI so purchased sheet stock (with vendor/PO/heat-lot provenance) can be added to inventory, distinct from the one-time `RecordOpeningBalance` census flow.

**Architecture:** A new `ReceiveMaterialService` in `MaterialLedger.Data/Receiving/` inserts a `SheetLot` (`SourceType = Receipt`), an `InventoryItem`, and a `MaterialTransaction` (`TransactionType = Receipt`) atomically, in the same transactional/idempotency shape every other write service in this codebase uses. A new `Pages/Receiving/{NewReceipt,Success}` pair, reached via a second action link on the existing `Census/SelectSlot` slot picker, drives it.

**Tech Stack:** ASP.NET Core Razor Pages, EF Core 9 / SQL Server, xUnit integration tests against a real SQL Server test database.

**Design doc:** `docs/superpowers/specs/2026-08-09-receive-material-design.md` — read it before starting if anything below is unclear; it has the full rationale.

## Global Constraints

- Every new write service follows this exact shape (copy `MaterialLedger.Data/Census/OpeningBalanceService.cs`): primary-constructor DI, SHA-256 request-payload hash, `BeginTransactionAsync(IsolationLevel.Serializable)`, `OperationLogGuard.CheckAsync` → replay-or-proceed, business mutation, `OperationLogGuard.Record` (sync, not `RecordAsync`), one final `SaveChangesAsync()`, `CommitAsync()`.
- Never trust a client-supplied `AppUserId` — always resolve the acting user via `User.FindFirst(ClaimTypes.NameIdentifier)` in the page handler, matching `NewOpeningBalanceModel.OnPostAsync`.
- Razor Pages forms: **never** combine `asp-page`/`asp-route-*` tag helpers with an explicit `@Html.AntiForgeryToken()` on the same `<form>` — the `FormTagHelper` auto-injects its own token whenever any `asp-*` attribute is present, and pairing it with an explicit token double-emits `__RequestVerificationToken` and breaks every submit. This has been hit 3 times already in this codebase. The correct pattern (used by `Census/NewOpeningBalance.cshtml`): plain `<form method="post">` with **no** `asp-*` attributes, plus a single explicit `@Html.AntiForgeryToken()`; route values travel via hidden `<input>` fields bound to `[BindProperty]`s instead.
- Tests are xUnit integration tests against a real SQL Server (`[Trait("Category", "Integration")]` + `IClassFixture<SqlServerTestDatabaseFixture>`), never mocks/in-memory. Requires the `MATERIALLEDGER_TEST_ADMIN_CONNECTION` environment variable to already be set in this dev environment (it is — prior phases' tests run this way).
- Grants for the live ShopOps `materialledger_app` login live in `docs/schema/material-permissions-vN.sql`, one new file per phase — never edit v1–v4. Applying a new grants file to the real Forge SQL Server is a manual, human-run administrative step; no automation in this repo runs it.

---

### Task 1: `ReceiveMaterialService`

**Files:**
- Create: `MaterialLedger.Data/Receiving/ReceiveMaterialService.cs`
- Create: `MaterialLedger.Tests/ReceiveMaterialServiceTests.cs`
- Modify: `MaterialLedger.Web/Program.cs:21` (add DI registration directly after the `OpeningBalanceService` line)

**Interfaces:**
- Produces: `MaterialLedger.Data.Receiving.ReceiveMaterialRequest` (record), `ReceiveMaterialResult` (class with `InventoryItemId`, `SheetLotId`, `PublicItemCode`), `ReceiveMaterialService(MaterialLedgerDbContext db, OperationLogGuard operationLog)` with `Task<ReceiveMaterialResult> ReceiveMaterialAsync(Guid correlationId, ReceiveMaterialRequest request, int receivedBy)`. Task 3 (the web page) consumes these exact names/signatures.

- [ ] **Step 1: Write the failing test file**

Create `MaterialLedger.Tests/ReceiveMaterialServiceTests.cs`:

```csharp
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Receiving;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class ReceiveMaterialServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public ReceiveMaterialServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long locationId, long specId)> SeedAsync(
        MaterialLedgerDbContext db, InventoryLocationType locationType = InventoryLocationType.Shelf)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Programmer, IsActive = true, PasswordHash = "x" };
        var location = new InventoryLocation
        {
            LocationCode = $"LOC-{Guid.NewGuid():N}"[..30], DisplayName = "Slot", LocationType = locationType,
            StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true
        };
        var guid = Guid.NewGuid().ToString("N");
        var spec = new MaterialSpec { MaterialFamily = $"Steel-{guid}"[..30], Grade = $"Grade-{guid}"[..30], ThicknessIn = 0.25m, DisplayName = "Test Material", IsActive = true };
        db.AddRange(user, location, spec);
        await db.SaveChangesAsync();
        return (user.AppUserId, location.InventoryLocationId, spec.MaterialSpecId);
    }

    private static ReceiveMaterialRequest MakeRequest(long locationId, long specId, decimal quantity = 6m,
        string purchaseOrderNumber = "PO-1001", InventoryItemKind itemKind = InventoryItemKind.FullSheetBundle, bool isQuarantined = false) =>
        new(locationId, specId, itemKind, quantity, null, null, ShapeClassification.Rectangular, isQuarantined,
            "Acme Steel", purchaseOrderNumber, "RCPT-1", "VP-1", "HEAT-1", 42.50m, "Received via dock door 2");

    [Fact]
    public async Task ReceiveMaterialAsync_FullSheetBundle_CreatesLotItemAndTransaction()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId);

        var result = await service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId);

        var item = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == result.InventoryItemId);
        Assert.Equal(6m, item.QuantityOnHand);
        Assert.Equal(InventoryItemStatus.Available, item.Status);
        Assert.StartsWith("SHEET-", item.PublicItemCode);
        Assert.Equal(result.SheetLotId, item.SheetLotId);

        var lot = await db.SheetLots.SingleAsync(x => x.SheetLotId == result.SheetLotId);
        Assert.Equal(SheetLotSourceType.Receipt, lot.SourceType);
        Assert.Equal("PO-1001", lot.PurchaseOrderNumber);
        Assert.Equal("Acme Steel", lot.VendorName);
        Assert.Equal(42.50m, lot.CostPerUnit);

        var transactions = await db.MaterialTransactions.Where(x => x.InventoryItemId == item.InventoryItemId).ToListAsync();
        Assert.Single(transactions);
        Assert.Equal(MaterialTransactionType.Receipt, transactions[0].TransactionType);
        Assert.Equal(result.SheetLotId, transactions[0].SheetLotId);
        Assert.Equal(6m, transactions[0].QuantityDelta);
    }

    [Fact]
    public async Task ReceiveMaterialAsync_RejectsDropItemKind()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, quantity: 1m, itemKind: InventoryItemKind.Drop);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task ReceiveMaterialAsync_RejectsMissingPurchaseOrderNumber()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, purchaseOrderNumber: "   ");

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task ReceiveMaterialAsync_RejectsFractionalQuantity()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, quantity: 2.5m);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task ReceiveMaterialAsync_RejectsQuantityZero()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, quantity: 0m);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task ReceiveMaterialAsync_NonLeafLocation_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _, specId) = await SeedAsync(db);
        var rack = new InventoryLocation
        {
            LocationCode = $"RACK-{Guid.NewGuid():N}"[..30], DisplayName = "Rack", LocationType = InventoryLocationType.Rack,
            StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true
        };
        db.Add(rack);
        await db.SaveChangesAsync();
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(rack.InventoryLocationId, specId, quantity: 1m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task ReceiveMaterialAsync_QuarantinedItem_IsNotAvailable()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId, isQuarantined: true);

        var result = await service.ReceiveMaterialAsync(Guid.NewGuid(), request, userId);

        var item = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == result.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Quarantined, item.Status);
    }

    [Fact]
    public async Task ReceiveMaterialAsync_RetryWithSameCorrelationId_DoesNotDuplicate()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new ReceiveMaterialService(db, new OperationLogGuard(db));
        var request = MakeRequest(locationId, specId);
        var correlationId = Guid.NewGuid();

        var first = await service.ReceiveMaterialAsync(correlationId, request, userId);
        var second = await service.ReceiveMaterialAsync(correlationId, request, userId);

        Assert.Equal(first.PublicItemCode, second.PublicItemCode);
        var itemCount = await db.InventoryItems.CountAsync(x => x.InventoryLocationId == locationId);
        Assert.Equal(1, itemCount);
        var lotCount = await db.SheetLots.CountAsync(x => x.MaterialSpecId == specId);
        Assert.Equal(1, lotCount);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail on a compile error**

Run: `dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --filter FullyQualifiedName~ReceiveMaterialServiceTests`
Expected: build FAILS — `MaterialLedger.Data.Receiving` namespace and `ReceiveMaterialService`/`ReceiveMaterialRequest` don't exist yet.

- [ ] **Step 3: Implement `ReceiveMaterialService`**

Create `MaterialLedger.Data/Receiving/ReceiveMaterialService.cs`:

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
    string? Notes);

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
            SourceType = SheetLotSourceType.Receipt
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

- [ ] **Step 4: Register the service in DI**

In `MaterialLedger.Web/Program.cs`, add a line directly after the existing `OpeningBalanceService` registration (currently line 21):

```csharp
builder.Services.AddScoped<MaterialLedger.Data.Census.OpeningBalanceService>();
builder.Services.AddScoped<MaterialLedger.Data.Receiving.ReceiveMaterialService>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --filter FullyQualifiedName~ReceiveMaterialServiceTests`
Expected: 8/8 PASS.

Then run the full suite to confirm no regressions:

Run: `dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj`
Expected: all tests pass (matches whatever the full-suite count was before this task — no regressions).

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Data/Receiving/ReceiveMaterialService.cs MaterialLedger.Tests/ReceiveMaterialServiceTests.cs MaterialLedger.Web/Program.cs
git commit -m "feat: add ReceiveMaterial write procedure"
```

---

### Task 2: Permissions grant file

**Files:**
- Create: `docs/schema/material-permissions-v5.sql`

**Interfaces:**
- Consumes: nothing from Task 1's code (this is a standalone SQL file, not compiled/run automatically).
- Produces: nothing consumed by later tasks — this is a leaf deliverable, reviewable independently of Task 1/3 passing.

- [ ] **Step 1: Write the grants file**

Create `docs/schema/material-permissions-v5.sql`:

```sql
-- docs/schema/material-permissions-v5.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, Phase 4 (ReceiveMaterial).
--
-- ReceiveMaterialService (MaterialLedger.Data/Receiving/ReceiveMaterialService.cs) inserts a new
-- SheetLot row for every receipt. material.SheetLots has had zero grants to materialledger_app
-- through v1-v4 -- this is the only new grant this file adds. It does not touch the Phase 0-3
-- grants already applied by material-permissions-v1.sql/v2.sql/v3.sql/v4.sql, and is safe to
-- re-run (GRANT is idempotent).
--
-- Run against the real `MaterialLedger` database using an account with GRANT rights (sa or a member
-- of db_owner/db_securityadmin), the same way material-permissions-v1.sql through v4.sql were
-- applied. This is a manual administrative step -- the running application never applies its own
-- grants.

USE MaterialLedger;
GO

-- SheetLots: ReceiveMaterialAsync inserts exactly one row per receipt, capturing vendor/PO/receipt/
-- vendor-part/heat-lot/cost provenance. No UPDATE/DELETE -- a SheetLot, once recorded, is never
-- edited or removed, matching every other provenance table in this schema.
GRANT SELECT, INSERT ON material.SheetLots TO materialledger_app;
GO

-- No new grant is needed on InventoryItems (Phase 1/2 already granted SELECT/INSERT/UPDATE, which
-- covers inserting the new received InventoryItem row), MaterialTransactions (Phase 0 already
-- granted SELECT/INSERT, covering the new Receipt transaction type), MaterialSpecs (Phase 1 already
-- granted SELECT/INSERT for the existing-vs-new-spec picker), or ItemCodeSeq (Phase 1 already
-- granted UPDATE, reused here to mint the new item's PublicItemCode).
```

- [ ] **Step 2: Commit**

```bash
git add docs/schema/material-permissions-v5.sql
git commit -m "docs: add v5 grants file for ReceiveMaterial (SheetLots)"
```

(This file is not applied automatically — applying it to the real Forge SQL Server is a separate manual step for a human with `db_owner`/`db_securityadmin` rights, per the Global Constraints above. Do not attempt to run it against forge-sql as part of this task.)

---

### Task 3: `Receiving` UI pages

**Files:**
- Create: `MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml`
- Create: `MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/Receiving/Success.cshtml`
- Create: `MaterialLedger.Web/Pages/Receiving/Success.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Census/SelectSlot.cshtml`

**Interfaces:**
- Consumes: `MaterialLedger.Data.Receiving.ReceiveMaterialService.ReceiveMaterialAsync(Guid, ReceiveMaterialRequest, int)` and `ReceiveMaterialRequest`/`ReceiveMaterialResult` from Task 1 (must be complete and committed first). Also consumes `MaterialLedger.Data.Materials.MaterialSpecService.SearchAsync(string?)` / `GetOrCreateAsync(...)` (existing, unchanged) and `MaterialLedgerPolicies.RequireProgrammerOrManager` (existing, unchanged).
- Produces: nothing consumed by later tasks — this is the final leaf of the plan.

There is no automated test coverage for Razor Pages in this codebase (confirmed: no `*PageTests.cs` files exist; only service-level integration tests do). Verification for this task is a clean `dotnet build` plus a manual walkthrough, matching how every prior Phase's page tasks (e.g. `Pages/Closeout/*`, `Pages/Exceptions/*`) were verified.

- [ ] **Step 1: Create the `NewReceipt` page model**

Create `MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml.cs`:

```csharp
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Materials;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Receiving;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Receiving;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class NewReceiptModel(MaterialLedgerDbContext db, ReceiveMaterialService receiveMaterial, MaterialSpecService materialSpecs) : PageModel
{
    [BindProperty] public long LocationId { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public long? ExistingMaterialSpecId { get; set; }
    [BindProperty] public string MaterialFamily { get; set; } = string.Empty;
    [BindProperty] public string? Grade { get; set; }
    [BindProperty] public string? Finish { get; set; }
    [BindProperty] public decimal ThicknessIn { get; set; }
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
    public List<MaterialSpec> AvailableSpecs { get; set; } = [];
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long locationId)
    {
        Location = await db.InventoryLocations.FindAsync(locationId);
        if (Location is null)
            return NotFound();

        LocationId = locationId;
        CorrelationId = Guid.NewGuid();
        AvailableSpecs = (await materialSpecs.SearchAsync(null)).ToList();

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Location = await db.InventoryLocations.FindAsync(LocationId);
        AvailableSpecs = (await materialSpecs.SearchAsync(null)).ToList();

        if (ExistingMaterialSpecId is null && (string.IsNullOrWhiteSpace(MaterialFamily) || ThicknessIn <= 0))
        {
            Error = "Select an existing material or enter a family and thickness for a new one.";
            return Page();
        }

        if (ExistingMaterialSpecId is not null && AvailableSpecs.All(s => s.MaterialSpecId != ExistingMaterialSpecId))
        {
            Error = "The selected existing material is no longer available. Please choose another.";
            return Page();
        }

        try
        {
            var specId = ExistingMaterialSpecId
                ?? (await materialSpecs.GetOrCreateAsync(MaterialFamily, Grade, Finish, ThicknessIn,
                    $"{MaterialFamily} {Grade} {ThicknessIn}in".Trim())).MaterialSpecId;

            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new ReceiveMaterialRequest(LocationId, specId, ItemKind, Quantity,
                UsableWidthIn, UsableLengthIn, ShapeClassification, IsQuarantined,
                VendorName, PurchaseOrderNumber, ReceiptNumber, VendorPartNumber, HeatLotNumber, CostPerUnit, Notes);

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
}
```

- [ ] **Step 2: Create the `NewReceipt` view**

Create `MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml`:

```html
@* MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml *@
@page
@model MaterialLedger.Web.Pages.Receiving.NewReceiptModel
@{ Layout = "Shared/_Layout"; }
<h1>Receive material — @Model.Location?.LocationCode</h1>

@if (Model.Error is not null)
{
    <p style="color:red">@Model.Error</p>
}

<form method="post">
    @Html.AntiForgeryToken()
    <input type="hidden" asp-for="LocationId" />
    <input type="hidden" asp-for="CorrelationId" />

    <fieldset>
        <legend>Material</legend>
        <label>Existing spec
            <select asp-for="ExistingMaterialSpecId">
                <option value="">-- new material --</option>
                @foreach (var spec in Model.AvailableSpecs)
                {
                    <option value="@spec.MaterialSpecId">@spec.DisplayName</option>
                }
            </select>
        </label>
        <p>Or enter a new material:</p>
        <label>Family <input asp-for="MaterialFamily" /></label>
        <label>Grade <input asp-for="Grade" /></label>
        <label>Finish <input asp-for="Finish" /></label>
        <label>Thickness (in) <input asp-for="ThicknessIn" type="number" step="0.0001" /></label>
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
        <label>Vendor <input asp-for="VendorName" /></label>
        <label>PO number (required) <input asp-for="PurchaseOrderNumber" /></label>
        <label>Receipt number <input asp-for="ReceiptNumber" /></label>
        <label>Vendor part number <input asp-for="VendorPartNumber" /></label>
        <label>Heat/lot number <input asp-for="HeatLotNumber" /></label>
        <label>Cost per unit <input asp-for="CostPerUnit" type="number" step="0.01" /></label>
    </fieldset>

    <label><input type="checkbox" asp-for="IsQuarantined" /> Damaged / questionable on arrival — quarantine (not available for reservation)</label>
    <label>Notes <textarea asp-for="Notes"></textarea></label>

    <button type="submit">Receive material</button>
</form>
```

Note: the `ItemKind` radio group deliberately omits a `Drop` option — the page never offers it, and `ReceiveMaterialService.Validate` rejects it server-side anyway (Task 1) as defense in depth.

- [ ] **Step 3: Create the `Success` page**

Create `MaterialLedger.Web/Pages/Receiving/Success.cshtml.cs`:

```csharp
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Receiving;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class SuccessModel : PageModel
{
    [BindProperty(SupportsGet = true)] public string Code { get; set; } = string.Empty;
    public void OnGet() { }
}
```

Create `MaterialLedger.Web/Pages/Receiving/Success.cshtml`:

```html
@* MaterialLedger.Web/Pages/Receiving/Success.cshtml *@
@page
@model MaterialLedger.Web.Pages.Receiving.SuccessModel
@{ Layout = "Shared/_Layout"; }
<h1>Material received</h1>
<p>Item code: <strong>@Model.Code</strong></p>
<p><a asp-page="/Census/SelectSlot">Receive another item</a></p>
```

- [ ] **Step 4: Add the entry-point link on `Census/SelectSlot.cshtml`**

In `MaterialLedger.Web/Pages/Census/SelectSlot.cshtml`, change:

```html
            <td><a asp-page="NewOpeningBalance" asp-route-locationId="@slot.InventoryLocationId">Record opening balance</a></td>
```

to:

```html
            <td>
                <a asp-page="NewOpeningBalance" asp-route-locationId="@slot.InventoryLocationId">Record opening balance</a>
                &nbsp;|&nbsp;
                <a asp-page="/Receiving/NewReceipt" asp-route-locationId="@slot.InventoryLocationId">Receive material</a>
            </td>
```

- [ ] **Step 5: Build and verify no errors**

Run: `dotnet build MaterialLedger.sln`
Expected: 0 errors, 0 warnings (matches the bar every prior Phase page task was held to per `docs/PROGRESS.md`'s session log).

- [ ] **Step 6: Manual walkthrough**

Run the app (`dotnet run --project MaterialLedger.Web`), log in as a Programmer or Manager user, and confirm:
1. `/Census/SelectSlot` shows both "Record opening balance" and "Receive material" links per slot.
2. Clicking "Receive material" opens `/Receiving/NewReceipt?locationId=...` with the location code in the heading.
3. Submitting with an empty PO number shows the "A purchase order number is required..." error and does not create a row.
4. Submitting a valid form (existing or new material spec, quantity, PO number) redirects to `/Receiving/Success?code=SHEET-...` showing the new item code.
5. Logged in as an Operator-only user, navigating directly to `/Receiving/NewReceipt?locationId=1` is rejected (403/redirect to login), confirming the `RequireProgrammerOrManager` policy is enforced.

- [ ] **Step 7: Commit**

```bash
git add MaterialLedger.Web/Pages/Receiving/ MaterialLedger.Web/Pages/Census/SelectSlot.cshtml
git commit -m "feat: add Receive material pages"
```

---

## Final verification

After all three tasks are committed, run the full test suite once more to confirm nothing drifted across tasks:

Run: `dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj`
Expected: all tests pass, including the 8 new `ReceiveMaterialServiceTests`.

This phase's remaining step — applying `docs/schema/material-permissions-v5.sql` to the real Forge SQL Server — is a manual, human-run administrative action outside the scope of any task above (per Global Constraints), to be done by the user when ready to use this feature against the live database.
