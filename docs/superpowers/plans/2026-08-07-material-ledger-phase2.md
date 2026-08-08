# Material Ledger Phase 2 — Reservations and Inventory View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement Phase 2 of `REMCO-Laser-Material-Ledger-Design-Spec.md` (section 7, "Phase 2 — reservations and inventory view"): build searchable current inventory and a location map with real availability counts, and implement the transactionally safe `CreateReservation` → `ApproveReservation` → `ReleaseReservation` write surface with stale/quarantine rejection and row-version concurrency, plus the job/drawing/revision/PEP reference fields and a manual (non-OpenNest-engine) expected-remainder estimate path.

**Architecture:** Two new read/write-surface types in `MaterialLedger.Data`: `MaterialLedger.Data.Inventory.InventoryQueryService` (read-only search/summary, no `OperationLogGuard` — same pattern as the existing `MaterialSpecService`) and `MaterialLedger.Data.Reservations.ReservationService` (all three write procedures — `CreateReservationAsync`, `ApproveReservationAsync`, `ReleaseReservationAsync` — each guarded by the existing `OperationLogGuard` idempotency mechanism inside a `Serializable` DB transaction, matching the pattern established in Phase 0/1). New Razor Pages under `Pages/Inventory` and `Pages/Reservations`, plus enhancements to the existing `Pages/Locations` pages to show real available/reserved/exception counts instead of the placeholder "slots confirmed" view. No EF Core migration is needed — `InventoryItems`, `MaterialReservations`, and `MaterialTransactions` already exist from the Phase 0 `InitialCreate` migration. Per the design spec's explicit fallback rule (section 5, "If OpenNest cannot return a serializable result, the reservation falls back to the approved manual estimate path"), this phase builds only the manual-estimate path for expected remainder (`ExpectedRemainderWidthIn`/`LengthIn`/`ExpectedRemainderSourceType = Manual`) — no OpenNest engine client exists in this repo, and none is added here; that integration is explicitly deferred.

**Tech Stack:** .NET 8, ASP.NET Core Razor Pages, EF Core 9 / SQL Server, xUnit (integration tests against a real SQL Server via `SqlServerTestDatabaseFixture`, following the existing Phase 0/1 test convention).

## Global Constraints

- All timestamps are UTC (`DateTime.UtcNow`), matching existing `datetime2` columns.
- Every new write procedure (`CreateReservation`, `ApproveReservation`, `ReleaseReservation`) MUST go through `OperationLogGuard` inside a caller-owned `Serializable` transaction, per spec section 3 ("Every procedure checks this table first") — this is not optional per-procedure. `InventoryQueryService` is read-only and does NOT use `OperationLogGuard`, matching the existing `MaterialSpecService` precedent.
- `CreateReservation`, `ApproveReservation`, `ReleaseReservation` are all Programmer/Manager-only per spec section 3's role table — enforce via `[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]` on the `Reservations/Create` page, and via an explicit in-handler role check (`User.IsInRole("Programmer") || User.IsInRole("Manager")`, else `Forbid()`) on the `Approve`/`Release` POST handlers of `Reservations/Details` — that page's `GET` stays `[Authorize]` (broad read visibility) so any signed-in role can view reservation state, matching spec section 6 ("Every screen shows only the actions the signed-in session's role permits"). Never trust a client-supplied `AppUserId`; always resolve it from `User.FindFirst(ClaimTypes.NameIdentifier)` server-side.
- **Unknown material is not available material** (spec section 1, rule 1): `CreateReservationAsync` must transactionally reject an item that is `Quarantined`, `Depleted`, `Scrapped`, already `Reserved`, or whose `LastObservedAtUtc` is older than the configurable freshness threshold (default 90 days, `ReservationFreshnessThresholdDays` in `appsettings.json`, wired into both `InventoryQueryService` and `ReservationService` at DI registration in `Program.cs` — never hardcoded past that one config read).
- **A reservation is not consumption** (spec section 1, rule 2): `CreateReservationAsync` never changes `InventoryItem.QuantityOnHand`. `ApproveReservationAsync` only flips `InventoryItem.Status` to `Reserved` for `Drop`/`FullSheet` items (never for `FullSheetBundle`, whose `Status` always stays `Available` — spec section 3, "Item-level status vs. reservation balance").
- **Availability formula** (spec section 3): available quantity = `QuantityOnHand` minus the count of that item's active (`Proposed`/`Approved`/`AwaitingCloseout`) reservations. This governs what `InventoryQueryService` *displays*. The *enforced* balance check — rejecting a second claim on the same unit — happens specifically inside `ApproveReservationAsync`, per spec section 3's "Approval" paragraph ("`CreateReservation` produces a `Proposed` reservation; it does not yet hold availability... `ApproveReservation`... is what actually counts against the availability formula"). `CreateReservationAsync` therefore does NOT block concurrent `Proposed` reservations against the same item — only `ApproveReservationAsync` does, via a `ReservationBalanceException`. This is a deliberate reading of an internally tense spec passage; the resulting behavior matches acceptance test 2 exactly (concurrent approval, not concurrent creation, is what must fail transactionally).
- Use optimistic concurrency (spec section 3, "Use optimistic concurrency (`rowversion`) on reservation/item changes; return a conflict rather than silently overwriting a newer operator action"): `ApproveReservationAsync`/`ReleaseReservationAsync` take an `expectedRowVersion` byte array from the caller (round-tripped through the UI as a hidden base64 field, exactly as `CorrelationId` is round-tripped today) and compare it against the freshly loaded reservation's `RowVersion` *before* mutating; a mismatch throws `ReservationConcurrencyException` rather than proceeding. `InventoryItem` mutations inside these procedures are always computed from freshly-read-within-transaction state, never from client-supplied item data, so no separate item-level rowversion parameter is needed.
- `ReleaseReservationAsync` is only valid while the reservation is `Proposed` or `Approved` — a reservation in `AwaitingCloseout` or any terminal status must be rejected transactionally (spec section 3, "Approval" paragraph; acceptance test 3).
- Follow existing repo conventions exactly: primary-constructor DI (`public class Foo(Dependency dep)`), nullable reference types enabled, `[Trait("Category", "Integration")]` + `SqlServerTestDatabaseFixture` for any test touching real SQL Server behavior, manual `@Html.AntiForgeryToken()` in forms (not the `asp-page` auto-token convention), and per-task commits in the style of the existing git history (e.g. `feat: add CreateReservation/ApproveReservation/ReleaseReservation write procedures with idempotency and real-SQL-Server tests`).
- Grants for the live ShopOps `materialledger_app` login arrive with the code that uses them (see the header comment in `docs/schema/material-permissions-v1.sql`) — the final task adds a new `docs/schema/material-permissions-v3.sql` rather than editing the Phase 0/1 files. Applying it to the real `MaterialLedger` database on forge-sql is a manual, human-run administrative step — do not attempt to run it against forge-sql automatically.

---

### Task 1: `InventoryQueryService` (searchable inventory + rack availability summaries)

**Files:**
- Create: `MaterialLedger.Data/Inventory/InventoryQueryService.cs`
- Modify: `MaterialLedger.Web/Program.cs`
- Modify: `MaterialLedger.Web/appsettings.json`
- Test: `MaterialLedger.Tests/InventoryQueryServiceTests.cs`

**Interfaces:**
- Produces: `MaterialFreshness.IsStale(DateTime lastObservedAtUtc, int freshnessThresholdDays)`, `InventoryFilter(string? MaterialFamily, string? Grade, string? Finish, decimal? ThicknessIn, InventoryItemKind? ItemKind, decimal? MinUsableWidthIn, decimal? MinUsableLengthIn, string? LocationCode, bool AvailableOnly)`, `InventoryItemSummary { long InventoryItemId; string PublicItemCode; string MaterialSpecDisplayName; InventoryItemKind ItemKind; InventoryItemStatus Status; decimal QuantityOnHand; decimal AvailableQuantity; decimal? UsableWidthIn; decimal? UsableLengthIn; string LocationCode; DateTime LastObservedAtUtc; string LastObservedByDisplayName; bool IsStale; }`, `LocationSummary { string LocationCode; string DisplayName; int AvailableItemCount; int ReservedItemCount; int ExceptionItemCount; }`, `InventoryQueryService.SearchAsync(InventoryFilter filter)`, `InventoryQueryService.GetItemsAtLocationsAsync(IReadOnlyList<long> locationIds)`, `InventoryQueryService.GetRackSummariesAsync()` — used by Task 2's Locations pages, Task 3's Inventory Search page, and Task 4's `ReservationService` (via `MaterialFreshness`).

- [ ] **Step 1: Write the failing test**

```csharp
// MaterialLedger.Tests/InventoryQueryServiceTests.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class InventoryQueryServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public InventoryQueryServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long rackId, long slotId, long specId)> SeedAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Programmer, IsActive = true, PasswordHash = "x" };
        var rack = new InventoryLocation { LocationCode = $"RACK-{Guid.NewGuid():N}"[..20], DisplayName = "Rack", LocationType = InventoryLocationType.Rack, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        db.AddRange(user, rack);
        await db.SaveChangesAsync();
        var slot = new InventoryLocation { LocationCode = $"{rack.LocationCode}:S01", DisplayName = "Shelf 1", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, ParentInventoryLocationId = rack.InventoryLocationId, IsActive = true };
        var spec = new MaterialSpec { MaterialFamily = "Carbon Steel", Grade = "A36", ThicknessIn = 0.25m, DisplayName = "A36 1/4in", IsActive = true };
        db.AddRange(slot, spec);
        await db.SaveChangesAsync();
        return (user.AppUserId, rack.InventoryLocationId, slot.InventoryLocationId, spec.MaterialSpecId);
    }

    private static InventoryItem NewBundle(long slotId, long specId, int observedBy, decimal qty) => new()
    {
        PublicItemCode = $"SHEET-{Guid.NewGuid():N}"[..20],
        MaterialSpecId = specId,
        InventoryLocationId = slotId,
        ItemKind = InventoryItemKind.FullSheetBundle,
        Status = InventoryItemStatus.Available,
        QuantityOnHand = qty,
        ShapeClassification = ShapeClassification.Rectangular,
        LastObservedAtUtc = DateTime.UtcNow,
        LastObservedBy = observedBy
    };

    [Fact]
    public async Task SearchAsync_ComputesAvailableQuantityMinusActiveReservations()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _, slotId, specId) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 6m);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        db.MaterialReservations.Add(new MaterialReservation { InventoryItemId = item.InventoryItemId, Status = ReservationStatus.Approved, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedBy = userId });
        db.MaterialReservations.Add(new MaterialReservation { InventoryItemId = item.InventoryItemId, Status = ReservationStatus.Proposed, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedBy = userId });
        db.MaterialReservations.Add(new MaterialReservation { InventoryItemId = item.InventoryItemId, Status = ReservationStatus.Released, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedBy = userId });
        await db.SaveChangesAsync();
        var service = new InventoryQueryService(db, freshnessThresholdDays: 90);

        var results = await service.SearchAsync(new InventoryFilter(null, null, null, null, null, null, null, null, false));

        var summary = Assert.Single(results);
        Assert.Equal(6m, summary.QuantityOnHand);
        Assert.Equal(4m, summary.AvailableQuantity); // 6 - (1 Approved + 1 Proposed); Released excluded
    }

    [Fact]
    public async Task SearchAsync_FiltersByMaterialFamilyAndMinWidth()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _, slotId, specId) = await SeedAsync(db);
        db.InventoryItems.Add(NewBundle(slotId, specId, userId, 3m));
        var otherSpec = new MaterialSpec { MaterialFamily = "Aluminum", Grade = "5052", ThicknessIn = 0.25m, DisplayName = "5052 1/4in", IsActive = true };
        db.MaterialSpecs.Add(otherSpec);
        await db.SaveChangesAsync();
        var drop = new InventoryItem
        {
            PublicItemCode = $"DROP-{Guid.NewGuid():N}"[..20], MaterialSpecId = otherSpec.MaterialSpecId, InventoryLocationId = slotId,
            ItemKind = InventoryItemKind.Drop, Status = InventoryItemStatus.Available, QuantityOnHand = 1,
            UsableWidthIn = 12m, UsableLengthIn = 24m, ShapeClassification = ShapeClassification.Rectangular,
            LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = userId
        };
        db.InventoryItems.Add(drop);
        await db.SaveChangesAsync();
        var service = new InventoryQueryService(db, freshnessThresholdDays: 90);

        var results = await service.SearchAsync(new InventoryFilter("Aluminum", null, null, null, null, 10m, null, null, false));

        var summary = Assert.Single(results);
        Assert.Equal(drop.InventoryItemId, summary.InventoryItemId);
    }

    [Fact]
    public async Task SearchAsync_FlagsStaleObservation()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _, slotId, specId) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 2m);
        item.LastObservedAtUtc = DateTime.UtcNow.AddDays(-91);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new InventoryQueryService(db, freshnessThresholdDays: 90);

        var results = await service.SearchAsync(new InventoryFilter(null, null, null, null, null, null, null, null, false));

        Assert.True(Assert.Single(results).IsStale);
    }

    [Fact]
    public async Task SearchAsync_AvailableOnly_ExcludesQuarantinedStaleAndFullyReserved()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _, slotId, specId) = await SeedAsync(db);
        var quarantined = NewBundle(slotId, specId, userId, 2m);
        quarantined.Status = InventoryItemStatus.Quarantined;
        var stale = NewBundle(slotId, specId, userId, 2m);
        stale.LastObservedAtUtc = DateTime.UtcNow.AddDays(-91);
        var fullyReserved = NewBundle(slotId, specId, userId, 1m);
        var available = NewBundle(slotId, specId, userId, 3m);
        db.InventoryItems.AddRange(quarantined, stale, fullyReserved, available);
        await db.SaveChangesAsync();
        db.MaterialReservations.Add(new MaterialReservation { InventoryItemId = fullyReserved.InventoryItemId, Status = ReservationStatus.Approved, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedBy = userId });
        await db.SaveChangesAsync();
        var service = new InventoryQueryService(db, freshnessThresholdDays: 90);

        var results = await service.SearchAsync(new InventoryFilter(null, null, null, null, null, null, null, null, true));

        var summary = Assert.Single(results);
        Assert.Equal(available.InventoryItemId, summary.InventoryItemId);
    }

    [Fact]
    public async Task GetRackSummariesAsync_CountsAvailableReservedAndExceptionsPerRack()
    {
        await using var db = _fixture.CreateContext();
        var (userId, rackId, slotId, specId) = await SeedAsync(db);
        var available = NewBundle(slotId, specId, userId, 5m);
        var quarantined = NewBundle(slotId, specId, userId, 1m);
        quarantined.Status = InventoryItemStatus.Quarantined;
        var reservedDrop = new InventoryItem
        {
            PublicItemCode = $"DROP-{Guid.NewGuid():N}"[..20], MaterialSpecId = specId, InventoryLocationId = slotId,
            ItemKind = InventoryItemKind.Drop, Status = InventoryItemStatus.Reserved, QuantityOnHand = 1,
            UsableWidthIn = 10m, UsableLengthIn = 10m, ShapeClassification = ShapeClassification.Rectangular,
            LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = userId
        };
        db.InventoryItems.AddRange(available, quarantined, reservedDrop);
        await db.SaveChangesAsync();
        var service = new InventoryQueryService(db, freshnessThresholdDays: 90);
        var rackCode = (await db.InventoryLocations.FindAsync(rackId))!.LocationCode;

        var summaries = await service.GetRackSummariesAsync();

        var rackSummary = summaries.Single(x => x.LocationCode == rackCode);
        Assert.Equal(1, rackSummary.AvailableItemCount);
        Assert.Equal(1, rackSummary.ReservedItemCount);
        Assert.Equal(1, rackSummary.ExceptionItemCount);
    }

    [Fact]
    public async Task GetItemsAtLocationsAsync_ReturnsOnlyItemsAtGivenLocations()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _, slotId, specId) = await SeedAsync(db);
        var inScope = NewBundle(slotId, specId, userId, 2m);
        var otherSlot = new InventoryLocation { LocationCode = $"OTHER-{Guid.NewGuid():N}"[..20], DisplayName = "Other", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        db.InventoryLocations.Add(otherSlot);
        await db.SaveChangesAsync();
        var outOfScope = NewBundle(otherSlot.InventoryLocationId, specId, userId, 2m);
        db.InventoryItems.AddRange(inScope, outOfScope);
        await db.SaveChangesAsync();
        var service = new InventoryQueryService(db, freshnessThresholdDays: 90);

        var results = await service.GetItemsAtLocationsAsync([slotId]);

        var summary = Assert.Single(results);
        Assert.Equal(inScope.InventoryItemId, summary.InventoryItemId);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter InventoryQueryServiceTests`
Expected: FAIL — `InventoryQueryService` does not exist yet (compile error).

- [ ] **Step 3: Implement `InventoryQueryService`**

```csharp
// MaterialLedger.Data/Inventory/InventoryQueryService.cs
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Inventory;

public static class MaterialFreshness
{
    public static bool IsStale(DateTime lastObservedAtUtc, int freshnessThresholdDays) =>
        (DateTime.UtcNow - lastObservedAtUtc).TotalDays > freshnessThresholdDays;
}

public record InventoryFilter(
    string? MaterialFamily,
    string? Grade,
    string? Finish,
    decimal? ThicknessIn,
    InventoryItemKind? ItemKind,
    decimal? MinUsableWidthIn,
    decimal? MinUsableLengthIn,
    string? LocationCode,
    bool AvailableOnly);

public class InventoryItemSummary
{
    public required long InventoryItemId { get; init; }
    public required string PublicItemCode { get; init; }
    public required string MaterialSpecDisplayName { get; init; }
    public required InventoryItemKind ItemKind { get; init; }
    public required InventoryItemStatus Status { get; init; }
    public required decimal QuantityOnHand { get; init; }
    public required decimal AvailableQuantity { get; init; }
    public decimal? UsableWidthIn { get; init; }
    public decimal? UsableLengthIn { get; init; }
    public required string LocationCode { get; init; }
    public required DateTime LastObservedAtUtc { get; init; }
    public required string LastObservedByDisplayName { get; init; }
    public required bool IsStale { get; init; }
}

public class LocationSummary
{
    public required string LocationCode { get; init; }
    public required string DisplayName { get; init; }
    public required int AvailableItemCount { get; init; }
    public required int ReservedItemCount { get; init; }
    public required int ExceptionItemCount { get; init; }
}

public class InventoryQueryService(MaterialLedgerDbContext db, int freshnessThresholdDays)
{
    private static readonly ReservationStatus[] ActiveReservationStatuses =
    [
        ReservationStatus.Proposed, ReservationStatus.Approved, ReservationStatus.AwaitingCloseout
    ];

    private record ItemRow(InventoryItem Item, MaterialSpec Spec, InventoryLocation Location, AppUser Observer, int ActiveCount);

    private IQueryable<ItemRow> BaseQuery()
    {
        var activeCounts = db.MaterialReservations
            .Where(r => ActiveReservationStatuses.Contains(r.Status))
            .GroupBy(r => r.InventoryItemId)
            .Select(g => new { InventoryItemId = g.Key, Count = g.Count() });

        return from item in db.InventoryItems
               join spec in db.MaterialSpecs on item.MaterialSpecId equals spec.MaterialSpecId
               join location in db.InventoryLocations on item.InventoryLocationId equals location.InventoryLocationId
               join observer in db.AppUsers on item.LastObservedBy equals observer.AppUserId
               join active in activeCounts on item.InventoryItemId equals active.InventoryItemId into activeJoin
               from active in activeJoin.DefaultIfEmpty()
               where item.Status != InventoryItemStatus.Depleted && item.Status != InventoryItemStatus.Scrapped
               select new ItemRow(item, spec, location, observer, active == null ? 0 : active.Count);
    }

    private InventoryItemSummary ToSummary(ItemRow row) => new()
    {
        InventoryItemId = row.Item.InventoryItemId,
        PublicItemCode = row.Item.PublicItemCode,
        MaterialSpecDisplayName = row.Spec.DisplayName,
        ItemKind = row.Item.ItemKind,
        Status = row.Item.Status,
        QuantityOnHand = row.Item.QuantityOnHand,
        AvailableQuantity = row.Item.QuantityOnHand - row.ActiveCount,
        UsableWidthIn = row.Item.UsableWidthIn,
        UsableLengthIn = row.Item.UsableLengthIn,
        LocationCode = row.Location.LocationCode,
        LastObservedAtUtc = row.Item.LastObservedAtUtc,
        LastObservedByDisplayName = row.Observer.DisplayName,
        IsStale = MaterialFreshness.IsStale(row.Item.LastObservedAtUtc, freshnessThresholdDays)
    };

    public async Task<IReadOnlyList<InventoryItemSummary>> SearchAsync(InventoryFilter filter)
    {
        var query = BaseQuery();
        if (!string.IsNullOrWhiteSpace(filter.MaterialFamily))
            query = query.Where(x => EF.Functions.Like(x.Spec.MaterialFamily, $"%{filter.MaterialFamily}%"));
        if (!string.IsNullOrWhiteSpace(filter.Grade))
            query = query.Where(x => x.Spec.Grade != null && EF.Functions.Like(x.Spec.Grade, $"%{filter.Grade}%"));
        if (!string.IsNullOrWhiteSpace(filter.Finish))
            query = query.Where(x => x.Spec.Finish != null && EF.Functions.Like(x.Spec.Finish, $"%{filter.Finish}%"));
        if (filter.ThicknessIn is not null)
            query = query.Where(x => x.Spec.ThicknessIn == filter.ThicknessIn);
        if (filter.ItemKind is not null)
            query = query.Where(x => x.Item.ItemKind == filter.ItemKind);
        if (filter.MinUsableWidthIn is not null)
            query = query.Where(x => x.Item.UsableWidthIn != null && x.Item.UsableWidthIn >= filter.MinUsableWidthIn);
        if (filter.MinUsableLengthIn is not null)
            query = query.Where(x => x.Item.UsableLengthIn != null && x.Item.UsableLengthIn >= filter.MinUsableLengthIn);
        if (!string.IsNullOrWhiteSpace(filter.LocationCode))
            query = query.Where(x => x.Location.LocationCode == filter.LocationCode);

        var rows = await query.ToListAsync();
        IEnumerable<InventoryItemSummary> results = rows.Select(ToSummary);
        if (filter.AvailableOnly)
            results = results.Where(x => x.AvailableQuantity > 0 && x.Status != InventoryItemStatus.Quarantined && !x.IsStale);
        return results.ToList();
    }

    public async Task<IReadOnlyList<InventoryItemSummary>> GetItemsAtLocationsAsync(IReadOnlyList<long> locationIds)
    {
        var rows = await BaseQuery().Where(x => locationIds.Contains(x.Location.InventoryLocationId)).ToListAsync();
        return rows.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<LocationSummary>> GetRackSummariesAsync()
    {
        var rows = await BaseQuery().ToListAsync();
        var racks = await db.InventoryLocations
            .Where(x => x.LocationType == InventoryLocationType.Rack && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToListAsync();

        return racks.Select(rack =>
        {
            var rackRows = rows.Where(r => (r.Location.ParentInventoryLocationId ?? r.Location.InventoryLocationId) == rack.InventoryLocationId).ToList();
            return new LocationSummary
            {
                LocationCode = rack.LocationCode,
                DisplayName = rack.DisplayName,
                AvailableItemCount = rackRows.Count(r => r.Item.QuantityOnHand - r.ActiveCount > 0 && r.Item.Status != InventoryItemStatus.Quarantined),
                ReservedItemCount = rackRows.Count(r => r.Item.Status == InventoryItemStatus.Reserved),
                ExceptionItemCount = rackRows.Count(r => r.Item.Status == InventoryItemStatus.Quarantined)
            };
        }).ToList();
    }
}
```

- [ ] **Step 4: Add the freshness-threshold setting and wire the service into `Program.cs`**

```json
// MaterialLedger.Web/appsettings.json — add alongside ConnectionStrings/SeedAccounts
{
  "ConnectionStrings": {
    "MaterialLedger": ""
  },
  "ReservationFreshnessThresholdDays": 90,
  "SeedAccounts": [
    { "Username": "programmer", "DisplayName": "Programmer", "Role": "Programmer", "InitialPassword": "" },
    { "Username": "operator", "DisplayName": "Operator", "Role": "Operator", "InitialPassword": "" },
    { "Username": "manager", "DisplayName": "Manager", "Role": "Manager", "InitialPassword": "" }
  ]
}
```

```csharp
// MaterialLedger.Web/Program.cs — add after the existing OpeningBalanceService registration
builder.Services.AddScoped(sp => new MaterialLedger.Data.Inventory.InventoryQueryService(
    sp.GetRequiredService<MaterialLedger.Data.MaterialLedgerDbContext>(),
    builder.Configuration.GetValue("ReservationFreshnessThresholdDays", 90)));
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test --filter InventoryQueryServiceTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Data/Inventory/InventoryQueryService.cs MaterialLedger.Web/Program.cs MaterialLedger.Web/appsettings.json MaterialLedger.Tests/InventoryQueryServiceTests.cs
git commit -m "feat: add InventoryQueryService for searchable inventory and rack availability summaries"
```

---

### Task 2: Locations pages show real availability/reserved/exception counts

**Files:**
- Modify: `MaterialLedger.Web/Pages/Locations/Index.cshtml`, `Index.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Locations/Details.cshtml`, `Details.cshtml.cs`

**Interfaces:**
- Consumes: `InventoryQueryService.GetRackSummariesAsync()`, `InventoryQueryService.GetItemsAtLocationsAsync(IReadOnlyList<long>)` (Task 1).

- [ ] **Step 1: Material map shows per-rack counts**

```csharp
// MaterialLedger.Web/Pages/Locations/Index.cshtml.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Locations;

[Authorize]
public class IndexModel(MaterialLedgerDbContext db, InventoryQueryService inventoryQuery) : PageModel
{
    public List<InventoryLocation> Racks { get; set; } = [];
    public Dictionary<string, LocationSummary> Summaries { get; set; } = [];

    public async Task OnGetAsync()
    {
        Racks = await db.InventoryLocations
            .Where(x => x.LocationType == InventoryLocationType.Rack && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToListAsync();
        Summaries = (await inventoryQuery.GetRackSummariesAsync()).ToDictionary(x => x.LocationCode);
    }
}
```

```html
@* MaterialLedger.Web/Pages/Locations/Index.cshtml *@
@page
@model MaterialLedger.Web.Pages.Locations.IndexModel
@{ Layout = "Shared/_Layout"; }
<h1>Material Map</h1>
<table>
    <thead><tr><th>Code</th><th>Rack</th><th>Storage</th><th>Slots confirmed</th><th>Available</th><th>Reserved</th><th>Exceptions</th></tr></thead>
    <tbody>
    @foreach (var rack in Model.Racks)
    {
        var summary = Model.Summaries.GetValueOrDefault(rack.LocationCode);
        <tr>
            <td>@rack.LocationCode</td>
            <td><a asp-page="Details" asp-route-code="@rack.LocationCode">@rack.DisplayName</a></td>
            <td>@rack.StorageMode</td>
            <td>@(rack.CapacityConfirmed ? "Yes" : "No")</td>
            <td>@(summary?.AvailableItemCount ?? 0)</td>
            <td>@(summary?.ReservedItemCount ?? 0)</td>
            <td>@(summary?.ExceptionItemCount ?? 0)</td>
        </tr>
    }
    </tbody>
</table>
```

- [ ] **Step 2: Rack detail shows the items at each slot**

```csharp
// MaterialLedger.Web/Pages/Locations/Details.cshtml.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Locations;

[Authorize]
public class DetailsModel(MaterialLedgerDbContext db, InventoryQueryService inventoryQuery) : PageModel
{
    public InventoryLocation? Rack { get; set; }
    public List<InventoryLocation> Slots { get; set; } = [];
    public ILookup<string, InventoryItemSummary> ItemsBySlotCode { get; set; } = Enumerable.Empty<InventoryItemSummary>().ToLookup(x => x.LocationCode);
    public bool CanConfirmSlots { get; set; }

    public async Task<IActionResult> OnGetAsync(string code)
    {
        Rack = await db.InventoryLocations.FirstOrDefaultAsync(x => x.LocationCode == code);
        if (Rack is null)
            return NotFound();

        Slots = await db.InventoryLocations
            .Where(x => x.ParentInventoryLocationId == Rack.InventoryLocationId && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToListAsync();
        var items = await inventoryQuery.GetItemsAtLocationsAsync(Slots.Select(x => x.InventoryLocationId).ToList());
        ItemsBySlotCode = items.ToLookup(x => x.LocationCode);
        CanConfirmSlots = !Rack.CapacityConfirmed && (User.IsInRole("Programmer") || User.IsInRole("Manager"));
        return Page();
    }
}
```

```html
@* MaterialLedger.Web/Pages/Locations/Details.cshtml *@
@page "{code}"
@model MaterialLedger.Web.Pages.Locations.DetailsModel
@{ Layout = "Shared/_Layout"; }
<h1>@Model.Rack!.DisplayName (@Model.Rack.LocationCode)</h1>
<p>Storage: @Model.Rack.StorageMode — Slots confirmed: @(Model.Rack.CapacityConfirmed ? "Yes" : "No")</p>
@if (Model.CanConfirmSlots)
{
    <p><a asp-page="ConfirmSlots" asp-route-code="@Model.Rack.LocationCode">Confirm slot count</a></p>
}
else if (Model.Rack.CapacityConfirmed)
{
    <p><a asp-page="Labels" asp-route-code="@Model.Rack.LocationCode">Printable labels</a></p>
}
<table>
    <thead><tr><th>Code</th><th>Name</th><th>Type</th><th>Items</th></tr></thead>
    <tbody>
    @foreach (var slot in Model.Slots)
    {
        <tr>
            <td>@slot.LocationCode</td>
            <td>@slot.DisplayName</td>
            <td>@slot.LocationType</td>
            <td>
            @foreach (var item in Model.ItemsBySlotCode[slot.LocationCode])
            {
                <div>@item.PublicItemCode — @item.MaterialSpecDisplayName — @item.AvailableQuantity of @item.QuantityOnHand — @item.Status @(item.IsStale ? "(STALE)" : "")</div>
            }
            </td>
        </tr>
    }
    </tbody>
</table>
<p><a asp-page="Index">Back to material map</a></p>
```

- [ ] **Step 3: Build and manually verify**

Run: `dotnet build`
Expected: builds with no errors.

- [ ] **Step 4: Commit**

```bash
git add MaterialLedger.Web/Pages/Locations/Index.cshtml MaterialLedger.Web/Pages/Locations/Index.cshtml.cs MaterialLedger.Web/Pages/Locations/Details.cshtml MaterialLedger.Web/Pages/Locations/Details.cshtml.cs
git commit -m "feat: show real available/reserved/exception counts on the material map and rack detail pages"
```

---

### Task 3: `Pages/Inventory/Search` — searchable current inventory page

**Files:**
- Create: `MaterialLedger.Web/Pages/Inventory/Search.cshtml`, `Search.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Index.cshtml`

**Interfaces:**
- Consumes: `InventoryQueryService.SearchAsync(InventoryFilter)` (Task 1).

- [ ] **Step 1: Implement the search page**

```csharp
// MaterialLedger.Web/Pages/Inventory/Search.cshtml.cs
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Inventory;

[Authorize]
public class SearchModel(InventoryQueryService inventoryQuery) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? MaterialFamily { get; set; }
    [BindProperty(SupportsGet = true)] public string? Grade { get; set; }
    [BindProperty(SupportsGet = true)] public string? Finish { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? ThicknessIn { get; set; }
    [BindProperty(SupportsGet = true)] public InventoryItemKind? ItemKind { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinUsableWidthIn { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinUsableLengthIn { get; set; }
    [BindProperty(SupportsGet = true)] public string? LocationCode { get; set; }
    [BindProperty(SupportsGet = true)] public bool AvailableOnly { get; set; } = true;

    public List<InventoryItemSummary> Results { get; set; } = [];
    public bool CanReserve { get; set; }

    public async Task OnGetAsync()
    {
        CanReserve = User.IsInRole("Programmer") || User.IsInRole("Manager");
        var filter = new InventoryFilter(MaterialFamily, Grade, Finish, ThicknessIn, ItemKind,
            MinUsableWidthIn, MinUsableLengthIn, LocationCode, AvailableOnly);
        Results = (await inventoryQuery.SearchAsync(filter)).ToList();
    }
}
```

```html
@* MaterialLedger.Web/Pages/Inventory/Search.cshtml *@
@page
@model MaterialLedger.Web.Pages.Inventory.SearchModel
@using MaterialLedger.Data.Models
@{ Layout = "Shared/_Layout"; }
<h1>Search inventory</h1>
<form method="get">
    <label>Family <input asp-for="MaterialFamily" /></label>
    <label>Grade <input asp-for="Grade" /></label>
    <label>Finish <input asp-for="Finish" /></label>
    <label>Thickness (in) <input asp-for="ThicknessIn" type="number" step="0.0001" /></label>
    <label>Kind
        <select asp-for="ItemKind">
            <option value="">-- any --</option>
            <option value="FullSheetBundle">Full sheet / bundle</option>
            <option value="FullSheet">Single full sheet</option>
            <option value="Drop">Drop</option>
        </select>
    </label>
    <label>Min usable width (in) <input asp-for="MinUsableWidthIn" type="number" step="0.001" /></label>
    <label>Min usable length (in) <input asp-for="MinUsableLengthIn" type="number" step="0.001" /></label>
    <label>Location code <input asp-for="LocationCode" /></label>
    <label><input asp-for="AvailableOnly" type="checkbox" /> Available only</label>
    <button type="submit">Search</button>
</form>

<table>
    <thead>
        <tr><th>Item</th><th>Material</th><th>Kind</th><th>Qty</th><th>Usable size</th><th>Location</th><th>Last observed</th><th>Status</th><th></th></tr>
    </thead>
    <tbody>
    @foreach (var r in Model.Results)
    {
        <tr style="@(r.IsStale ? "background:#fee" : "")">
            <td>@r.PublicItemCode</td>
            <td>@r.MaterialSpecDisplayName</td>
            <td>@r.ItemKind</td>
            <td>@r.AvailableQuantity of @r.QuantityOnHand</td>
            <td>@(r.UsableWidthIn is not null ? $"{r.UsableWidthIn} x {r.UsableLengthIn}" : "-")</td>
            <td>@r.LocationCode</td>
            <td>@r.LastObservedAtUtc.ToString("u") by @r.LastObservedByDisplayName @(r.IsStale ? "(STALE)" : "")</td>
            <td>@r.Status</td>
            <td>
            @if (Model.CanReserve && r.AvailableQuantity > 0 && !r.IsStale && r.Status != InventoryItemStatus.Quarantined)
            {
                <a asp-page="/Reservations/Create" asp-route-inventoryItemId="@r.InventoryItemId">Reserve</a>
            }
            </td>
        </tr>
    }
    </tbody>
</table>
```

- [ ] **Step 2: Add navigation from the home page**

```html
@* MaterialLedger.Web/Pages/Index.cshtml *@
@page
@model MaterialLedger.Web.Pages.IndexModel
@{ Layout = "Shared/_Layout"; }
<h1>Material Ledger</h1>
<ul>
    <li><a asp-page="/Locations/Index">Material map</a></li>
    <li><a asp-page="/Inventory/Search">Search inventory</a></li>
    @if (User.IsInRole("Programmer") || User.IsInRole("Manager"))
    {
        <li><a asp-page="/Census/SelectSlot">Opening census</a></li>
    }
</ul>
```

- [ ] **Step 3: Build and manually verify**

Run: `dotnet build`
Expected: builds with no errors. Once a reachable dev SQL Server connection string is configured, run the app and confirm `/Inventory/Search` renders and filters results, and that the "Reserve" link only appears for a Programmer/Manager session on non-stale, non-quarantined, available items.

- [ ] **Step 4: Commit**

```bash
git add MaterialLedger.Web/Pages/Inventory MaterialLedger.Web/Pages/Index.cshtml
git commit -m "feat: add searchable inventory page"
```

---

### Task 4: `ReservationService` — `CreateReservation`/`ApproveReservation`/`ReleaseReservation` write procedures

**Files:**
- Create: `MaterialLedger.Data/Reservations/ReservationService.cs`
- Modify: `MaterialLedger.Web/Program.cs`
- Test: `MaterialLedger.Tests/ReservationServiceTests.cs`

**Interfaces:**
- Consumes: `MaterialLedgerDbContext`, `OperationLogGuard` (existing), `MaterialFreshness.IsStale` (Task 1).
- Produces: `CreateReservationRequest(long InventoryItemId, string? ShopOpsJobId, string? DrawingId, string? DrawingRevision, ReservationEquipment? Equipment, int? PepNestYear, string? PepNestName, string? LaserProgramRecordId, decimal? ExpectedRemainderWidthIn, decimal? ExpectedRemainderLengthIn, ExpectedRemainderSource? ExpectedRemainderSourceType)`, `ReservationResult { long MaterialReservationId; ReservationStatus Status; }`, `ReservationBalanceException`, `ReservationConcurrencyException`, `ReservationService.CreateReservationAsync(Guid correlationId, CreateReservationRequest request, int createdBy)`, `ReservationService.ApproveReservationAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int approvedBy)`, `ReservationService.ReleaseReservationAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int releasedBy, string? notes)` — all used by Task 5's Create page and Task 6's Details page.

- [ ] **Step 1: Write the failing tests**

```csharp
// MaterialLedger.Tests/ReservationServiceTests.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class ReservationServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public ReservationServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long slotId, long specId)> SeedAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Programmer, IsActive = true, PasswordHash = "x" };
        var slot = new InventoryLocation { LocationCode = $"SLOT-{Guid.NewGuid():N}"[..30], DisplayName = "Slot", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        var spec = new MaterialSpec { MaterialFamily = "Carbon Steel", Grade = "A36", ThicknessIn = 0.25m, DisplayName = "A36 1/4in", IsActive = true };
        db.AddRange(user, slot, spec);
        await db.SaveChangesAsync();
        return (user.AppUserId, slot.InventoryLocationId, spec.MaterialSpecId);
    }

    private static InventoryItem NewBundle(long slotId, long specId, int observedBy, decimal qty, DateTime? lastObservedAtUtc = null) => new()
    {
        PublicItemCode = $"SHEET-{Guid.NewGuid():N}"[..20],
        MaterialSpecId = specId,
        InventoryLocationId = slotId,
        ItemKind = InventoryItemKind.FullSheetBundle,
        Status = InventoryItemStatus.Available,
        QuantityOnHand = qty,
        ShapeClassification = ShapeClassification.Rectangular,
        LastObservedAtUtc = lastObservedAtUtc ?? DateTime.UtcNow,
        LastObservedBy = observedBy
    };

    private static InventoryItem NewDrop(long slotId, long specId, int observedBy, InventoryItemStatus status = InventoryItemStatus.Available) => new()
    {
        PublicItemCode = $"DROP-{Guid.NewGuid():N}"[..20],
        MaterialSpecId = specId,
        InventoryLocationId = slotId,
        ItemKind = InventoryItemKind.Drop,
        Status = status,
        QuantityOnHand = status == InventoryItemStatus.Depleted ? 0 : 1,
        UsableWidthIn = 12m,
        UsableLengthIn = 24m,
        ShapeClassification = ShapeClassification.Rectangular,
        LastObservedAtUtc = DateTime.UtcNow,
        LastObservedBy = observedBy
    };

    private static CreateReservationRequest RequestFor(long itemId) =>
        new(itemId, "JOB-1", "DWG-1", "A", ReservationEquipment.Laser, 2026, "NEST-1", null, null, null, null);

    [Fact]
    public async Task CreateReservationAsync_Available_CreatesProposedReservationAndTransaction()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);

        var result = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);

        var reservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == result.MaterialReservationId);
        Assert.Equal(ReservationStatus.Proposed, reservation.Status);
        Assert.Equal(1, reservation.ReservedQuantity);
        var txns = await db.MaterialTransactions.Where(x => x.MaterialReservationId == reservation.MaterialReservationId).ToListAsync();
        Assert.Single(txns);
        Assert.Equal(MaterialTransactionType.Reservation, txns[0].TransactionType);
        Assert.Equal(0m, txns[0].QuantityDelta);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Available, itemAfter.Status); // Create alone never claims availability
    }

    [Fact]
    public async Task CreateReservationAsync_QuarantinedItem_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId));
    }

    [Fact]
    public async Task CreateReservationAsync_StaleObservation_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 5m, DateTime.UtcNow.AddDays(-91));
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId));
    }

    [Fact]
    public async Task CreateReservationAsync_RetryWithSameCorrelationId_DoesNotDuplicate()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var correlationId = Guid.NewGuid();

        var first = await service.CreateReservationAsync(correlationId, RequestFor(item.InventoryItemId), userId);
        var second = await service.CreateReservationAsync(correlationId, RequestFor(item.InventoryItemId), userId);

        Assert.Equal(first.MaterialReservationId, second.MaterialReservationId);
        var count = await db.MaterialReservations.CountAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ApproveReservationAsync_DropAvailable_SetsApprovedAndReservesItem()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var created = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var reservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == created.MaterialReservationId);

        var result = await service.ApproveReservationAsync(Guid.NewGuid(), reservation.MaterialReservationId, reservation.RowVersion, userId);

        Assert.Equal(ReservationStatus.Approved, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Reserved, itemAfter.Status);
    }

    [Fact]
    public async Task ApproveReservationAsync_SecondApprovalForSameDrop_FailsTransactionally()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var first = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var second = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var firstReservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == first.MaterialReservationId);
        var secondReservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == second.MaterialReservationId);

        await service.ApproveReservationAsync(Guid.NewGuid(), firstReservation.MaterialReservationId, firstReservation.RowVersion, userId);

        await Assert.ThrowsAsync<ReservationBalanceException>(() =>
            service.ApproveReservationAsync(Guid.NewGuid(), secondReservation.MaterialReservationId, secondReservation.RowVersion, userId));
        var reservationRows = await db.MaterialReservations.Where(x => x.InventoryItemId == item.InventoryItemId).ToListAsync();
        Assert.Single(reservationRows, x => x.Status == ReservationStatus.Approved);
        Assert.Single(reservationRows, x => x.Status == ReservationStatus.Proposed);
    }

    [Fact]
    public async Task ApproveReservationAsync_BundleExceedingOnHand_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 1m);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var first = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var second = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var firstReservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == first.MaterialReservationId);
        var secondReservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == second.MaterialReservationId);
        await service.ApproveReservationAsync(Guid.NewGuid(), firstReservation.MaterialReservationId, firstReservation.RowVersion, userId);

        await Assert.ThrowsAsync<ReservationBalanceException>(() =>
            service.ApproveReservationAsync(Guid.NewGuid(), secondReservation.MaterialReservationId, secondReservation.RowVersion, userId));
    }

    [Fact]
    public async Task ApproveReservationAsync_StaleRowVersion_ThrowsConcurrencyException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var created = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var staleRowVersion = new byte[8];

        await Assert.ThrowsAsync<ReservationConcurrencyException>(() =>
            service.ApproveReservationAsync(Guid.NewGuid(), created.MaterialReservationId, staleRowVersion, userId));
    }

    [Fact]
    public async Task ApproveReservationAsync_AlreadyApproved_ThrowsInvalidOperation()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var created = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var reservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == created.MaterialReservationId);
        await service.ApproveReservationAsync(Guid.NewGuid(), reservation.MaterialReservationId, reservation.RowVersion, userId);
        var approved = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApproveReservationAsync(Guid.NewGuid(), approved.MaterialReservationId, approved.RowVersion, userId));
    }

    [Fact]
    public async Task ReleaseReservationAsync_Approved_RestoresItemAvailabilityAndWritesTransaction()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var created = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var proposed = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == created.MaterialReservationId);
        await service.ApproveReservationAsync(Guid.NewGuid(), proposed.MaterialReservationId, proposed.RowVersion, userId);
        var approved = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == created.MaterialReservationId);

        var result = await service.ReleaseReservationAsync(Guid.NewGuid(), approved.MaterialReservationId, approved.RowVersion, userId, "no longer needed");

        Assert.Equal(ReservationStatus.Released, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Available, itemAfter.Status);
        var releaseTxns = await db.MaterialTransactions.Where(x =>
            x.MaterialReservationId == approved.MaterialReservationId && x.TransactionType == MaterialTransactionType.ReservationRelease).ToListAsync();
        Assert.Single(releaseTxns);
    }

    [Fact]
    public async Task ReleaseReservationAsync_AwaitingCloseout_ThrowsInvalidOperation()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var created = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var proposed = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == created.MaterialReservationId);
        await service.ApproveReservationAsync(Guid.NewGuid(), proposed.MaterialReservationId, proposed.RowVersion, userId);
        var reservation = await db.MaterialReservations.SingleAsync(x => x.MaterialReservationId == created.MaterialReservationId);
        reservation.Status = ReservationStatus.AwaitingCloseout;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReleaseReservationAsync(Guid.NewGuid(), reservation.MaterialReservationId, reservation.RowVersion, userId, null));
    }

    [Fact]
    public async Task ReleaseReservationAsync_StaleRowVersion_ThrowsConcurrencyException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new ReservationService(db, new OperationLogGuard(db), freshnessThresholdDays: 90);
        var created = await service.CreateReservationAsync(Guid.NewGuid(), RequestFor(item.InventoryItemId), userId);
        var staleRowVersion = new byte[8];

        await Assert.ThrowsAsync<ReservationConcurrencyException>(() =>
            service.ReleaseReservationAsync(Guid.NewGuid(), created.MaterialReservationId, staleRowVersion, userId, null));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter ReservationServiceTests`
Expected: FAIL — `ReservationService` does not exist yet (compile error).

- [ ] **Step 3: Implement `ReservationService`**

```csharp
// MaterialLedger.Data/Reservations/ReservationService.cs
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Reservations;

public record CreateReservationRequest(
    long InventoryItemId,
    string? ShopOpsJobId,
    string? DrawingId,
    string? DrawingRevision,
    ReservationEquipment? Equipment,
    int? PepNestYear,
    string? PepNestName,
    string? LaserProgramRecordId,
    decimal? ExpectedRemainderWidthIn,
    decimal? ExpectedRemainderLengthIn,
    ExpectedRemainderSource? ExpectedRemainderSourceType);

public class ReservationResult
{
    public required long MaterialReservationId { get; init; }
    public required ReservationStatus Status { get; init; }
}

public class ReservationBalanceException(string message) : Exception(message);
public class ReservationConcurrencyException(string message) : Exception(message);

public class ReservationService(MaterialLedgerDbContext db, OperationLogGuard operationLog, int freshnessThresholdDays)
{
    private static readonly ReservationStatus[] BundleActiveStatuses =
    [
        ReservationStatus.Approved, ReservationStatus.AwaitingCloseout
    ];

    public async Task<ReservationResult> CreateReservationAsync(Guid correlationId, CreateReservationRequest request, int createdBy)
    {
        var payloadHash = HashPayload(request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "CreateReservation", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<ReservationResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var item = await db.InventoryItems.FirstOrDefaultAsync(x => x.InventoryItemId == request.InventoryItemId)
            ?? throw new InvalidOperationException($"Inventory item {request.InventoryItemId} does not exist.");

        if (item.Status is InventoryItemStatus.Quarantined)
            throw new InvalidOperationException($"Item '{item.PublicItemCode}' is quarantined and cannot be reserved.");
        if (item.Status is InventoryItemStatus.Depleted or InventoryItemStatus.Scrapped)
            throw new InvalidOperationException($"Item '{item.PublicItemCode}' is {item.Status} and cannot be reserved.");
        if (item.Status is InventoryItemStatus.Reserved)
            throw new InvalidOperationException($"Item '{item.PublicItemCode}' is already reserved.");
        if (MaterialFreshness.IsStale(item.LastObservedAtUtc, freshnessThresholdDays))
            throw new InvalidOperationException(
                $"Item '{item.PublicItemCode}' was last observed {item.LastObservedAtUtc:u}, past the {freshnessThresholdDays}-day freshness threshold, and cannot be reserved without a fresh observation.");

        var now = DateTime.UtcNow;
        var reservation = new MaterialReservation
        {
            InventoryItemId = item.InventoryItemId,
            ShopOpsJobId = request.ShopOpsJobId,
            DrawingId = request.DrawingId,
            DrawingRevision = request.DrawingRevision,
            Equipment = request.Equipment,
            PepNestYear = request.PepNestYear,
            PepNestName = request.PepNestName,
            LaserProgramRecordId = request.LaserProgramRecordId,
            ReservedQuantity = 1,
            Status = ReservationStatus.Proposed,
            CreatedAtUtc = now,
            CreatedBy = createdBy,
            ExpectedRemainderWidthIn = request.ExpectedRemainderWidthIn,
            ExpectedRemainderLengthIn = request.ExpectedRemainderLengthIn,
            ExpectedRemainderSourceType = request.ExpectedRemainderSourceType
        };
        db.MaterialReservations.Add(reservation);
        await db.SaveChangesAsync();

        db.MaterialTransactions.Add(new MaterialTransaction
        {
            OccurredAtUtc = now,
            RecordedAtUtc = now,
            RecordedBy = createdBy,
            TransactionType = MaterialTransactionType.Reservation,
            InventoryItemId = item.InventoryItemId,
            MaterialReservationId = reservation.MaterialReservationId,
            QuantityDelta = 0,
            SourceSystem = MaterialTransactionSourceSystem.OperatorUI
        });

        var result = new ReservationResult { MaterialReservationId = reservation.MaterialReservationId, Status = reservation.Status };
        operationLog.Record(correlationId, "CreateReservation", payloadHash, JsonSerializer.Serialize(result), createdBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    public async Task<ReservationResult> ApproveReservationAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int approvedBy)
    {
        var payloadHash = HashPayload(new { materialReservationId, expectedRowVersion = Convert.ToHexString(expectedRowVersion) });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "ApproveReservation", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<ReservationResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var reservation = await db.MaterialReservations.FirstOrDefaultAsync(x => x.MaterialReservationId == materialReservationId)
            ?? throw new InvalidOperationException($"Reservation {materialReservationId} does not exist.");

        if (!reservation.RowVersion.SequenceEqual(expectedRowVersion))
            throw new ReservationConcurrencyException(
                $"Reservation {materialReservationId} was changed by someone else since it was loaded. Refresh and try again.");

        if (reservation.Status != ReservationStatus.Proposed)
            throw new InvalidOperationException(
                $"Reservation {materialReservationId} is {reservation.Status}, not Proposed — only a Proposed reservation can be approved.");

        var item = await db.InventoryItems.FirstAsync(x => x.InventoryItemId == reservation.InventoryItemId);

        if (item.ItemKind is InventoryItemKind.Drop or InventoryItemKind.FullSheet)
        {
            if (item.Status != InventoryItemStatus.Available)
                throw new ReservationBalanceException(
                    $"Item '{item.PublicItemCode}' is no longer available to approve this reservation against (current status: {item.Status}).");
            item.Status = InventoryItemStatus.Reserved;
        }
        else
        {
            var activeCount = await db.MaterialReservations.CountAsync(r =>
                r.InventoryItemId == item.InventoryItemId && BundleActiveStatuses.Contains(r.Status));
            if (activeCount + 1 > item.QuantityOnHand)
                throw new ReservationBalanceException(
                    $"Item '{item.PublicItemCode}' has {item.QuantityOnHand - activeCount} of {item.QuantityOnHand} available — insufficient balance to approve this reservation.");
        }

        reservation.Status = ReservationStatus.Approved;
        reservation.ApprovedAtUtc = DateTime.UtcNow;
        reservation.ApprovedBy = approvedBy;

        var result = new ReservationResult { MaterialReservationId = reservation.MaterialReservationId, Status = reservation.Status };
        operationLog.Record(correlationId, "ApproveReservation", payloadHash, JsonSerializer.Serialize(result), approvedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    public async Task<ReservationResult> ReleaseReservationAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int releasedBy, string? notes)
    {
        var payloadHash = HashPayload(new { materialReservationId, expectedRowVersion = Convert.ToHexString(expectedRowVersion), notes });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "ReleaseReservation", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<ReservationResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var reservation = await db.MaterialReservations.FirstOrDefaultAsync(x => x.MaterialReservationId == materialReservationId)
            ?? throw new InvalidOperationException($"Reservation {materialReservationId} does not exist.");

        if (!reservation.RowVersion.SequenceEqual(expectedRowVersion))
            throw new ReservationConcurrencyException(
                $"Reservation {materialReservationId} was changed by someone else since it was loaded. Refresh and try again.");

        if (reservation.Status is not (ReservationStatus.Proposed or ReservationStatus.Approved))
            throw new InvalidOperationException(
                $"Reservation {materialReservationId} is {reservation.Status} — only a Proposed or Approved reservation can be released.");

        var item = await db.InventoryItems.FirstAsync(x => x.InventoryItemId == reservation.InventoryItemId);
        if (item.ItemKind is InventoryItemKind.Drop or InventoryItemKind.FullSheet && item.Status == InventoryItemStatus.Reserved)
            item.Status = InventoryItemStatus.Available;

        reservation.Status = ReservationStatus.Released;

        var now = DateTime.UtcNow;
        db.MaterialTransactions.Add(new MaterialTransaction
        {
            OccurredAtUtc = now,
            RecordedAtUtc = now,
            RecordedBy = releasedBy,
            TransactionType = MaterialTransactionType.ReservationRelease,
            InventoryItemId = item.InventoryItemId,
            MaterialReservationId = reservation.MaterialReservationId,
            QuantityDelta = 0,
            Notes = notes,
            SourceSystem = MaterialTransactionSourceSystem.OperatorUI
        });

        var result = new ReservationResult { MaterialReservationId = reservation.MaterialReservationId, Status = reservation.Status };
        operationLog.Record(correlationId, "ReleaseReservation", payloadHash, JsonSerializer.Serialize(result), releasedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static string HashPayload<T>(T payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
}
```

- [ ] **Step 4: Wire it into `Program.cs`**

```csharp
// MaterialLedger.Web/Program.cs — add after the InventoryQueryService registration
builder.Services.AddScoped(sp => new MaterialLedger.Data.Reservations.ReservationService(
    sp.GetRequiredService<MaterialLedger.Data.MaterialLedgerDbContext>(),
    sp.GetRequiredService<MaterialLedger.Data.Idempotency.OperationLogGuard>(),
    builder.Configuration.GetValue("ReservationFreshnessThresholdDays", 90)));
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter ReservationServiceTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Data/Reservations/ReservationService.cs MaterialLedger.Web/Program.cs MaterialLedger.Tests/ReservationServiceTests.cs
git commit -m "feat: add CreateReservation/ApproveReservation/ReleaseReservation write procedures with idempotency and real-SQL-Server tests"
```

---

### Task 5: `Pages/Reservations/Create` — reservation creation page

**Files:**
- Create: `MaterialLedger.Web/Pages/Reservations/Create.cshtml`, `Create.cshtml.cs`

**Interfaces:**
- Consumes: `ReservationService.CreateReservationAsync` (Task 4), `MaterialLedgerPolicies.RequireProgrammerOrManager` (existing).

- [ ] **Step 1: Implement the create page**

```csharp
// MaterialLedger.Web/Pages/Reservations/Create.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Reservations;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class CreateModel(MaterialLedgerDbContext db, ReservationService reservations) : PageModel
{
    [BindProperty] public long InventoryItemId { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public string? ShopOpsJobId { get; set; }
    [BindProperty] public string? DrawingId { get; set; }
    [BindProperty] public string? DrawingRevision { get; set; }
    [BindProperty] public ReservationEquipment? Equipment { get; set; }
    [BindProperty] public int? PepNestYear { get; set; }
    [BindProperty] public string? PepNestName { get; set; }
    [BindProperty] public string? LaserProgramRecordId { get; set; }
    [BindProperty] public bool HasManualEstimate { get; set; }
    [BindProperty] public decimal? ExpectedRemainderWidthIn { get; set; }
    [BindProperty] public decimal? ExpectedRemainderLengthIn { get; set; }

    public InventoryItem? Item { get; set; }
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long inventoryItemId)
    {
        Item = await db.InventoryItems.Include(x => x.MaterialSpec).Include(x => x.InventoryLocation)
            .FirstOrDefaultAsync(x => x.InventoryItemId == inventoryItemId);
        if (Item is null)
            return NotFound();
        InventoryItemId = inventoryItemId;
        CorrelationId = Guid.NewGuid();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Item = await db.InventoryItems.Include(x => x.MaterialSpec).Include(x => x.InventoryLocation)
            .FirstOrDefaultAsync(x => x.InventoryItemId == InventoryItemId);
        if (Item is null)
            return NotFound();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new CreateReservationRequest(
                InventoryItemId, ShopOpsJobId, DrawingId, DrawingRevision, Equipment,
                PepNestYear, PepNestName, LaserProgramRecordId,
                HasManualEstimate ? ExpectedRemainderWidthIn : null,
                HasManualEstimate ? ExpectedRemainderLengthIn : null,
                HasManualEstimate ? ExpectedRemainderSource.Manual : null);

            var result = await reservations.CreateReservationAsync(CorrelationId, request, userId);
            return RedirectToPage("Details", new { id = result.MaterialReservationId });
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationConflictException)
        {
            Error = ex.Message;
            return Page();
        }
    }
}
```

```html
@* MaterialLedger.Web/Pages/Reservations/Create.cshtml *@
@page "{inventoryItemId:long?}"
@model MaterialLedger.Web.Pages.Reservations.CreateModel
@{ Layout = "Shared/_Layout"; }
<h1>Reserve @Model.Item?.PublicItemCode</h1>
<p>@Model.Item?.MaterialSpec?.DisplayName — @Model.Item?.InventoryLocation?.LocationCode — @Model.Item?.QuantityOnHand on hand</p>
@if (Model.Error is not null)
{
    <p style="color:red">@Model.Error</p>
}
<form method="post">
    @Html.AntiForgeryToken()
    <input type="hidden" asp-for="InventoryItemId" />
    <input type="hidden" asp-for="CorrelationId" />

    <fieldset>
        <legend>Job / drawing / nest reference</legend>
        <label>ShopOps job ID <input asp-for="ShopOpsJobId" /></label>
        <label>Drawing ID <input asp-for="DrawingId" /></label>
        <label>Drawing revision <input asp-for="DrawingRevision" /></label>
        <label>Equipment
            <select asp-for="Equipment">
                <option value="">-- unspecified --</option>
                <option value="Laser">Laser</option>
                <option value="Plasma">Plasma</option>
                <option value="Shear">Shear</option>
                <option value="Other">Other</option>
            </select>
        </label>
        <label>PEP nest year <input asp-for="PepNestYear" type="number" /></label>
        <label>PEP nest name <input asp-for="PepNestName" /></label>
        <label>Laser program record ID <input asp-for="LaserProgramRecordId" /></label>
    </fieldset>

    <fieldset>
        <legend>Expected remainder</legend>
        <label><input asp-for="HasManualEstimate" type="checkbox" /> Record an approved manual remainder estimate</label>
        <label>Expected width (in) <input asp-for="ExpectedRemainderWidthIn" type="number" step="0.001" /></label>
        <label>Expected length (in) <input asp-for="ExpectedRemainderLengthIn" type="number" step="0.001" /></label>
        <p><em>OpenNest engine integration is not built yet — this records an operator-approved manual estimate only (spec section 5).</em></p>
    </fieldset>

    <button type="submit">Create reservation (Proposed)</button>
</form>
```

- [ ] **Step 2: Build and manually verify**

Run: `dotnet build`
Expected: builds with no errors. Once a reachable dev SQL Server connection string is configured, confirm navigating from `/Inventory/Search`'s "Reserve" link to `/Reservations/Create/{id}` pre-fills the item summary and that submitting creates a `Proposed` reservation.

- [ ] **Step 3: Commit**

```bash
git add MaterialLedger.Web/Pages/Reservations/Create.cshtml MaterialLedger.Web/Pages/Reservations/Create.cshtml.cs
git commit -m "feat: add reservation creation page with job/drawing/PEP reference and manual remainder estimate fields"
```

---

### Task 6: `Pages/Reservations/Index` + `Details` — reservation list, approve, and release

**Files:**
- Create: `MaterialLedger.Web/Pages/Reservations/Index.cshtml`, `Index.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/Reservations/Details.cshtml`, `Details.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Index.cshtml`

**Interfaces:**
- Consumes: `ReservationService.ApproveReservationAsync`, `ReservationService.ReleaseReservationAsync`, `ReservationBalanceException`, `ReservationConcurrencyException` (Task 4).

- [ ] **Step 1: Reservations list**

```csharp
// MaterialLedger.Web/Pages/Reservations/Index.cshtml.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Reservations;

[Authorize]
public class IndexModel(MaterialLedgerDbContext db) : PageModel
{
    public List<MaterialReservation> Reservations { get; set; } = [];

    public async Task OnGetAsync()
    {
        Reservations = await db.MaterialReservations
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Where(x => x.Status == ReservationStatus.Proposed
                || x.Status == ReservationStatus.Approved
                || x.Status == ReservationStatus.AwaitingCloseout)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync();
    }
}
```

```html
@* MaterialLedger.Web/Pages/Reservations/Index.cshtml *@
@page
@model MaterialLedger.Web.Pages.Reservations.IndexModel
@{ Layout = "Shared/_Layout"; }
<h1>Reservations</h1>
<table>
    <thead><tr><th>Item</th><th>Material</th><th>Job/Drawing</th><th>Equipment</th><th>Status</th><th>Created</th><th></th></tr></thead>
    <tbody>
    @foreach (var r in Model.Reservations)
    {
        <tr>
            <td>@r.InventoryItem?.PublicItemCode</td>
            <td>@r.InventoryItem?.MaterialSpec?.DisplayName</td>
            <td>@r.ShopOpsJobId @r.DrawingId @r.DrawingRevision</td>
            <td>@r.Equipment</td>
            <td>@r.Status</td>
            <td>@r.CreatedAtUtc.ToString("u")</td>
            <td><a asp-page="Details" asp-route-id="@r.MaterialReservationId">View</a></td>
        </tr>
    }
    </tbody>
</table>
```

- [ ] **Step 2: Reservation detail with Approve/Release actions**

```csharp
// MaterialLedger.Web/Pages/Reservations/Details.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Reservations;

[Authorize]
public class DetailsModel(MaterialLedgerDbContext db, ReservationService reservations) : PageModel
{
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public string ExpectedRowVersionBase64 { get; set; } = string.Empty;
    [BindProperty] public string? ReleaseNotes { get; set; }

    public MaterialReservation? Reservation { get; set; }
    public bool CanAct { get; set; }
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        Reservation = await LoadAsync(id);
        if (Reservation is null)
            return NotFound();
        CanAct = User.IsInRole("Programmer") || User.IsInRole("Manager");
        CorrelationId = Guid.NewGuid();
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(long id)
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager"))
            return Forbid();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await reservations.ApproveReservationAsync(CorrelationId, id, Convert.FromBase64String(ExpectedRowVersionBase64), userId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ReservationBalanceException
            or ReservationConcurrencyException or OperationConflictException)
        {
            return await ShowErrorAsync(id, ex.Message);
        }

        return RedirectToPage("Details", new { id });
    }

    public async Task<IActionResult> OnPostReleaseAsync(long id)
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager"))
            return Forbid();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await reservations.ReleaseReservationAsync(CorrelationId, id, Convert.FromBase64String(ExpectedRowVersionBase64), userId, ReleaseNotes);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ReservationConcurrencyException or OperationConflictException)
        {
            return await ShowErrorAsync(id, ex.Message);
        }

        return RedirectToPage("Details", new { id });
    }

    private async Task<IActionResult> ShowErrorAsync(long id, string message)
    {
        Reservation = await LoadAsync(id);
        CanAct = true;
        CorrelationId = Guid.NewGuid();
        Error = message;
        return Page();
    }

    private Task<MaterialReservation?> LoadAsync(long id) =>
        db.MaterialReservations
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.InventoryLocation)
            .Include(x => x.CreatedByUser)
            .Include(x => x.ApprovedByUser)
            .FirstOrDefaultAsync(x => x.MaterialReservationId == id);
}
```

```html
@* MaterialLedger.Web/Pages/Reservations/Details.cshtml *@
@page "{id:long}"
@model MaterialLedger.Web.Pages.Reservations.DetailsModel
@using MaterialLedger.Data.Models
@{ Layout = "Shared/_Layout"; }
<h1>Reservation @Model.Reservation!.MaterialReservationId</h1>
@if (Model.Error is not null)
{
    <p style="color:red">@Model.Error</p>
}
<dl>
    <dt>Item</dt><dd>@Model.Reservation.InventoryItem?.PublicItemCode — @Model.Reservation.InventoryItem?.MaterialSpec?.DisplayName — @Model.Reservation.InventoryItem?.InventoryLocation?.LocationCode</dd>
    <dt>Job / Drawing</dt><dd>@Model.Reservation.ShopOpsJobId @Model.Reservation.DrawingId @Model.Reservation.DrawingRevision</dd>
    <dt>Equipment</dt><dd>@Model.Reservation.Equipment</dd>
    <dt>PEP nest</dt><dd>@Model.Reservation.PepNestYear @Model.Reservation.PepNestName</dd>
    <dt>Expected remainder</dt><dd>@(Model.Reservation.ExpectedRemainderWidthIn is not null ? $"{Model.Reservation.ExpectedRemainderWidthIn} x {Model.Reservation.ExpectedRemainderLengthIn} ({Model.Reservation.ExpectedRemainderSourceType})" : "-")</dd>
    <dt>Status</dt><dd>@Model.Reservation.Status</dd>
    <dt>Created</dt><dd>@Model.Reservation.CreatedAtUtc.ToString("u") by @Model.Reservation.CreatedByUser?.DisplayName</dd>
    <dt>Approved</dt><dd>@(Model.Reservation.ApprovedAtUtc is not null ? $"{Model.Reservation.ApprovedAtUtc:u} by {Model.Reservation.ApprovedByUser?.DisplayName}" : "-")</dd>
</dl>

@if (Model.CanAct && Model.Reservation.Status == ReservationStatus.Proposed)
{
    <form method="post" asp-page-handler="Approve" asp-route-id="@Model.Reservation.MaterialReservationId">
        @Html.AntiForgeryToken()
        <input type="hidden" asp-for="CorrelationId" />
        <input type="hidden" name="ExpectedRowVersionBase64" value="@Convert.ToBase64String(Model.Reservation.RowVersion)" />
        <button type="submit">Approve</button>
    </form>
}
@if (Model.CanAct && (Model.Reservation.Status == ReservationStatus.Proposed || Model.Reservation.Status == ReservationStatus.Approved))
{
    <form method="post" asp-page-handler="Release" asp-route-id="@Model.Reservation.MaterialReservationId">
        @Html.AntiForgeryToken()
        <input type="hidden" asp-for="CorrelationId" />
        <input type="hidden" name="ExpectedRowVersionBase64" value="@Convert.ToBase64String(Model.Reservation.RowVersion)" />
        <label>Notes <input asp-for="ReleaseNotes" /></label>
        <button type="submit">Release</button>
    </form>
}
<p><a asp-page="Index">Back to reservations</a></p>
```

- [ ] **Step 3: Add navigation from the home page**

```html
@* MaterialLedger.Web/Pages/Index.cshtml *@
@page
@model MaterialLedger.Web.Pages.IndexModel
@{ Layout = "Shared/_Layout"; }
<h1>Material Ledger</h1>
<ul>
    <li><a asp-page="/Locations/Index">Material map</a></li>
    <li><a asp-page="/Inventory/Search">Search inventory</a></li>
    <li><a asp-page="/Reservations/Index">Reservations</a></li>
    @if (User.IsInRole("Programmer") || User.IsInRole("Manager"))
    {
        <li><a asp-page="/Census/SelectSlot">Opening census</a></li>
    }
</ul>
```

- [ ] **Step 4: Build and manually verify**

Run: `dotnet build`
Expected: builds with no errors. Once a reachable dev SQL Server connection string is configured, confirm: (a) a Programmer session sees Approve/Release buttons on `/Reservations/Details/{id}` for a `Proposed` reservation and an Operator session does not; (b) approving flips a Drop item's location-card status to "Reserved" on `/Locations/Details`; (c) posting to the `Approve` handler with a role other than Programmer/Manager (e.g. via a modified request) returns 403 Forbidden.

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/Reservations/Index.cshtml MaterialLedger.Web/Pages/Reservations/Index.cshtml.cs MaterialLedger.Web/Pages/Reservations/Details.cshtml MaterialLedger.Web/Pages/Reservations/Details.cshtml.cs MaterialLedger.Web/Pages/Index.cshtml
git commit -m "feat: add reservations list and detail pages with role-gated approve/release actions"
```

---

### Task 7: Phase 2 ShopOps grants

**Files:**
- Create: `docs/schema/material-permissions-v3.sql`

- [ ] **Step 1: Write the grants script**

```sql
-- docs/schema/material-permissions-v3.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, Phase 2.
--
-- Phase 2 introduces the reservations/inventory-view write surface: InventoryQueryService
-- (read-only search/summary — no new grants needed beyond existing SELECT), and
-- ReservationService.CreateReservationAsync/ApproveReservationAsync/ReleaseReservationAsync.
-- Per the note at the top of material-permissions-v1.sql, grants arrive with the code that uses
-- them — this file adds ONLY what Phase 2 needs. It does not touch the Phase 0/1 grants already
-- applied by material-permissions-v1.sql/v2.sql, and is safe to re-run (GRANT is idempotent).
--
-- Run against the real `MaterialLedger` database using an account with GRANT rights (sa or a member
-- of db_owner/db_securityadmin), the same way material-permissions-v1.sql/v2.sql were applied. This
-- is a manual administrative step — the running application never applies its own grants.

USE MaterialLedger;
GO

-- InventoryItems: Phase 1 only ever created opening-balance rows (SELECT, INSERT). Phase 2's
-- ApproveReservation/ReleaseReservation flip Status between Available and Reserved for Drop/
-- FullSheet items, so UPDATE is now required. Still no DELETE — items are never removed, only
-- transitioned through status.
GRANT UPDATE ON material.InventoryItems TO materialledger_app;

-- MaterialReservations: new table for Phase 2. CreateReservation inserts; ApproveReservation and
-- ReleaseReservation update Status/ApprovedAtUtc/ApprovedBy on the same row (the RowVersion
-- concurrency check is enforced in application code, not by SQL Server). No DELETE — a released
-- reservation stays as history, matching every other table in this schema.
GRANT SELECT, INSERT, UPDATE ON material.MaterialReservations TO materialledger_app;
GO

-- No new grant is needed on MaterialTransactions: Phase 0's material-permissions-v1.sql already
-- granted SELECT, INSERT (with UPDATE/DELETE denied) at the table level, and the Reservation/
-- ReservationRelease transaction types written by this phase use that same existing grant.
```

- [ ] **Step 2: Commit**

```bash
git add docs/schema/material-permissions-v3.sql
git commit -m "docs: add Phase 2 ShopOps grants for reservations and item status updates"
```
