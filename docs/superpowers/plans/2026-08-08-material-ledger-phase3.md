# Material Ledger Phase 3 — Manual Closeout and Reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement Phase 3 of `REMCO-Laser-Material-Ledger-Design-Spec.md` (section 7, "Phase 3 — manual closeout and reconciliation"): build the manual closeout screen (`Approved`/`AwaitingCloseout` reservation list, optional `Start job` flag, `Close material` action), implement all four closeout outcomes (`AllConsumed`, `FullSheetReturned`, `UsableDropCreated`, `NeedsReview`) atomically via a new `CloseoutConsumption` write procedure, and build the exception queue with `ResolveMaterialException`. The controlled-pilot verification step (spec section 7, Phase 3 item 3) is included as a final manual checklist task — it requires a real job run on real equipment and cannot be automated in this repo.

**Architecture:** Two new write/read-surface types in `MaterialLedger.Data`, following the exact pattern established in Phases 0–2 (primary-constructor DI, SHA-256 payload hash, `BeginTransactionAsync(IsolationLevel.Serializable)`, `OperationLogGuard.CheckAsync` → replay-or-proceed, business mutation, `OperationLogGuard.Record`, one `SaveChangesAsync()`, `CommitAsync()`):
- `MaterialLedger.Data.Closeout.CloseoutService` — `MarkReservationAwaitingCloseoutAsync` and `CloseoutConsumptionAsync` (all four outcomes). Reuses `MaterialLedger.Data.Reservations.ReservationResult` and `ReservationConcurrencyException` rather than duplicating those types.
- `MaterialLedger.Data.Exceptions.MaterialExceptionService` — `ScanAsync` (on-demand detector for `QuarantinedMaterial`, `StaleObservation`, `AwaitingCloseoutOverdue`; run at the top of the Exceptions page's `OnGetAsync`, no background job), `GetOpenExceptionsAsync`, `GetRecentlyResolvedAsync`, and `ResolveMaterialExceptionAsync`. `OutcomeUnknown` exceptions are opened directly and atomically by `CloseoutService.CloseoutConsumptionAsync`'s `NeedsReview` branch, not by the scan.

New Razor Pages under `Pages/Closeout` (operator-facing, `RequireOperatorOrManager`) and `Pages/Exceptions` (viewable by any signed-in role; resolve action `RequireProgrammerOrManager`). No EF Core migration is needed — `MaterialExceptions` and `ProductionConsumptions` already exist from the Phase 0 `InitialCreate` migration as inert tables; this phase is the first to write to them.

**Tech Stack:** .NET 8, ASP.NET Core Razor Pages, EF Core 9 / SQL Server, xUnit (integration tests against a real SQL Server via `SqlServerTestDatabaseFixture`, following the existing Phase 0–2 test convention).

## Global Constraints

- All timestamps are UTC (`DateTime.UtcNow`), matching existing `datetime2` columns.
- Every new write procedure (`MarkReservationAwaitingCloseout`, `CloseoutConsumption`, `ResolveMaterialException`) MUST go through `OperationLogGuard` inside a caller-owned `Serializable` transaction — this is not optional per-procedure, per spec section 3. `MaterialExceptionService.ScanAsync` is a detector, not a user-invoked action with its own correlation id; it does NOT use `OperationLogGuard` — its own idempotency comes from the `MaterialException.DedupeKey` filtered-unique-index-on-`Open` (already present in the schema from Phase 0), checked in-memory before insert and enforced again by the DB as the final backstop.
- Role restrictions (spec section 3's role table), enforced server-side, never just hidden in the UI: `MarkReservationAwaitingCloseout` and `CloseoutConsumption` are Operator/Manager only (`[Authorize(Policy = MaterialLedgerPolicies.RequireOperatorOrManager)]` on `Pages/Closeout/*`). `ResolveMaterialException` is Programmer/Manager only (in-handler `User.IsInRole("Programmer") || User.IsInRole("Manager")` check on `Pages/Exceptions/Index`'s `OnPostResolveAsync`, matching the existing `Reservations/Details` pattern of a broadly-viewable GET with a role-gated POST). Never trust a client-supplied `AppUserId`; always resolve it from `User.FindFirst(ClaimTypes.NameIdentifier)` server-side.
- `CloseoutConsumption` is only callable against a reservation in `Approved` or `AwaitingCloseout` status (spec section 3's "Approval" paragraph; acceptance test 5) — any other status throws `InvalidOperationException` transactionally. `MarkReservationAwaitingCloseout` is only callable against `Approved` (moving it to `AwaitingCloseout`); it is optional and purely for floor visibility, never a precondition for closeout.
- **Outcome → transaction/state mapping is exactly the table in spec section 3** (`ProductionConsumption` doc comment), reproduced here as the binding contract for `CloseoutConsumptionAsync`:
  - `AllConsumed` → `Consumption` transaction, `QuantityOnHand -= 1`; additionally `Status → Depleted` for `Drop`/`FullSheet` items (individually-tracked, quantity always 1) but NOT for `FullSheetBundle` (which stays whatever it already was — never forced to `Depleted` on reaching zero, since the spec gives no such rule). Reservation → `ClosedConsumed`.
  - `FullSheetReturned` → `Return` transaction, `QuantityDelta = 0`, `LastObservedAtUtc`/`LastObservedBy` refreshed on the source item; `Status → Available` for `Drop`/`FullSheet` items only (a `FullSheetBundle`'s `Status` never changed by a reservation in the first place). Reservation → `ClosedReturned`. This only covers the "returned to the slot the bundle is already in" case (spec section 4D) — CloseoutConsumption has no alternate-destination parameter for this outcome; a physical move to a different slot after this outcome uses the existing `TransferInventory`-shaped workflow, which remains out of Phase 3 scope per the spec's own phase list.
  - `UsableDropCreated` → source decremented exactly like `AllConsumed` (same `Consumption` transaction + same `Status → Depleted` rule for individually-tracked kinds), PLUS a new `InventoryItem` (`ItemKind = Drop`, `QuantityOnHand = 1`, inherits the source's `SheetLotId`) at the operator-chosen leaf destination location, PLUS a `RemnantCreated` transaction linking the new item back to the source via `RelatedInventoryItemId` and to the reservation via `MaterialReservationId`. Reservation → `ClosedConsumed` (material was consumed; the remnant is a distinct new item, not the "same sheet" continuing). `ProductionConsumption.PrimaryMaterialTransactionId` points at the `Consumption` transaction, per spec ("found via shared `MaterialReservationId`, not a second FK here" for the `RemnantCreated` row).
  - `NeedsReview` → `Quarantine` transaction, `QuantityDelta = 0`, source item `Status → Quarantined`. Reservation → `Exception`. Atomically opens a `MaterialException` (`ExceptionType = OutcomeUnknown`, dedupe key `OutcomeUnknown:Reservation:{reservationId}`) in the SAME transaction — acceptance test 6 requires the exception to exist as a direct consequence of this outcome, not a later scan.
- `ProductionConsumption`'s unique index on `MaterialReservationId` (already in the schema) is the DB-level backstop against a duplicate closeout row; the primary idempotency mechanism a caller relies on is `OperationLogGuard` replay via the same `CorrelationId` (matches acceptance test 4's literal wording "retrying... with the same reservation" — i.e., the UI resubmitting the exact same already-submitted form).
- Use optimistic concurrency exactly like `ReservationService`: `MarkReservationAwaitingCloseoutAsync`/`CloseoutConsumptionAsync` take an `expectedRowVersion` byte array (round-tripped through the UI as a hidden base64 field) compared against the freshly-loaded reservation's `RowVersion` before mutating; mismatch throws `ReservationConcurrencyException` (reused from `MaterialLedger.Data.Reservations`, not duplicated).
- A destination location for `UsableDropCreated` must be a leaf location (`Shelf`, `Bay`, `Staging`, `Quarantine`, or `Scrap`; never `Area` or `Rack`), enforced in `CloseoutConsumptionAsync` itself — same `LeafLocationTypes` set and same enforcement style as `OpeningBalanceService` (spec section 3: "enforced in the write procedures, not just UI filtering").
- `MaterialExceptionService`'s two new configurable thresholds live in `appsettings.json` next to the existing `ReservationFreshnessThresholdDays`: reuse that same value for `StaleObservation` detection (do not introduce a second stale-threshold config — spec section 1 rule 1 already defines one freshness threshold, used consistently everywhere), and add a new `CloseoutOverdueThresholdDays` (default `1`) for `AwaitingCloseoutOverdue` — the spec requires this threshold be configurable but does not give a default number the way it does for freshness (90 days), so `1` day is a judgment call recorded in `docs/PROGRESS.md`, not a spec mandate; revisit with REMCO if the pilot shows it's noisy.
- `ResolveMaterialExceptionAsync` marks the exception `Fixed` with actor/timestamp/resolution notes only — it does NOT automatically revert the underlying item/reservation state (e.g., resolving a `QuarantinedMaterial` exception does not un-quarantine the item). No `ConfirmObservation`/`RecordAdjustment` procedure exists yet to re-validate physical state, and auto-reverting would violate spec section 1 rule 1. A resolved-but-still-broken condition legitimately reopens (new row, dedupe key match) on the next scan, matching spec section 4E's literal wording ("resurface only if their underlying condition returns").
- `GeometryMismatchOrInsufficientBalance` has NO automatic detector built in this phase — it's a valid `MaterialExceptionType` enum value with nothing populating it. This is a deliberate scope limit (no Phase 3 acceptance test requires it), not an oversight — do not add speculative detection code for it.
- Follow existing repo conventions exactly: primary-constructor DI (`public class Foo(Dependency dep)`), nullable reference types enabled, `[Trait("Category", "Integration")]` + `SqlServerTestDatabaseFixture` for any test touching real SQL Server behavior, manual `@Html.AntiForgeryToken()` in forms (never duplicated — one token per `<form>`): use a raw `action="?handler=..."` query string on the `<form>` tag, NOT the `asp-page-handler`/`asp-route-id` tag helpers — `FormTagHelper` auto-injects its own antiforgery token whenever an `asp-*` attribute is present on a `method="post"` form, so combining it with an explicit `@Html.AntiForgeryToken()` double-emits the token and breaks the submit (this exact defect was already found and fixed twice in this codebase's history, commits `24c1f54` and `841a4c2` — corrected here after Task 3's review caught it recurring in this plan's own sample code). A route id needed by the target handler travels either in the page's own URL (for `@page "{id:long}"` detail pages, matching `Reservations/Details.cshtml`) or as a plain hidden `<input>` field named to match the handler's parameter (for list pages with no id in their own URL). And per-task commits in the style of the existing git history.
- Grants for the live ShopOps `materialledger_app` login arrive with the code that uses them (see the header comment in `docs/schema/material-permissions-v1.sql`) — the final task adds `docs/schema/material-permissions-v4.sql` rather than editing v1–v3. Applying it to the real `MaterialLedger` database on forge-sql is a manual, human-run administrative step — do not attempt to run it against forge-sql automatically.

---

### Task 1: `CloseoutService` — `MarkReservationAwaitingCloseout` and `CloseoutConsumption` write procedures

**Files:**
- Create: `MaterialLedger.Data/Closeout/CloseoutService.cs`
- Modify: `MaterialLedger.Web/Program.cs`
- Test: `MaterialLedger.Tests/CloseoutServiceTests.cs`

**Interfaces:**
- Consumes: `MaterialLedgerDbContext`, `OperationLogGuard` (existing), `MaterialLedger.Data.Reservations.ReservationResult`, `ReservationConcurrencyException` (existing, Phase 2).
- Produces: `CloseoutConsumptionRequest(long MaterialReservationId, byte[] ExpectedRowVersion, ProductionConsumptionOutcome Outcome, decimal? MeasuredUsableWidthIn, decimal? MeasuredUsableLengthIn, long? DestinationLocationId, string? Notes)`, `CloseoutResult { long MaterialReservationId; ReservationStatus Status; string? RemnantPublicItemCode; }`, `CloseoutService.MarkReservationAwaitingCloseoutAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int actorId)`, `CloseoutService.CloseoutConsumptionAsync(Guid correlationId, CloseoutConsumptionRequest request, int confirmedBy)` — used by Task 3's Closeout pages.

- [ ] **Step 1: Write the failing tests**

```csharp
// MaterialLedger.Tests/CloseoutServiceTests.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Closeout;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class CloseoutServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public CloseoutServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long slotId, long destinationSlotId, long specId, long lotId)> SeedAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Operator, IsActive = true, PasswordHash = "x" };
        var slot = new InventoryLocation { LocationCode = $"SLOT-{Guid.NewGuid():N}"[..30], DisplayName = "Slot", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        var destinationSlot = new InventoryLocation { LocationCode = $"DEST-{Guid.NewGuid():N}"[..30], DisplayName = "Destination", LocationType = InventoryLocationType.Bay, StorageMode = InventoryStorageMode.VerticalDropBay, IsActive = true };
        var guid = Guid.NewGuid();
        var spec = new MaterialSpec { MaterialFamily = $"Carbon Steel-{guid}"[..30], Grade = $"A36-{guid}"[..30], ThicknessIn = 0.25m, DisplayName = "A36 1/4in", IsActive = true };
        db.AddRange(user, slot, destinationSlot, spec);
        await db.SaveChangesAsync();
        var lot = new SheetLot { MaterialSpecId = spec.MaterialSpecId, SourceType = SheetLotSourceType.OpeningBalance, HeatLotNumber = "HEAT-1" };
        db.SheetLots.Add(lot);
        await db.SaveChangesAsync();
        return (user.AppUserId, slot.InventoryLocationId, destinationSlot.InventoryLocationId, spec.MaterialSpecId, lot.SheetLotId);
    }

    private static InventoryItem NewBundle(long slotId, long specId, int observedBy, decimal qty, long? lotId = null) => new()
    {
        PublicItemCode = $"SHEET-{Guid.NewGuid():N}"[..20],
        MaterialSpecId = specId,
        SheetLotId = lotId,
        InventoryLocationId = slotId,
        ItemKind = InventoryItemKind.FullSheetBundle,
        Status = InventoryItemStatus.Available,
        QuantityOnHand = qty,
        ShapeClassification = ShapeClassification.Rectangular,
        LastObservedAtUtc = DateTime.UtcNow,
        LastObservedBy = observedBy
    };

    private static InventoryItem NewDrop(long slotId, long specId, int observedBy, long? lotId = null) => new()
    {
        PublicItemCode = $"DROP-{Guid.NewGuid():N}"[..20],
        MaterialSpecId = specId,
        SheetLotId = lotId,
        InventoryLocationId = slotId,
        ItemKind = InventoryItemKind.Drop,
        Status = InventoryItemStatus.Reserved,
        QuantityOnHand = 1,
        UsableWidthIn = 12m,
        UsableLengthIn = 24m,
        ShapeClassification = ShapeClassification.Rectangular,
        LastObservedAtUtc = DateTime.UtcNow,
        LastObservedBy = observedBy
    };

    private async Task<MaterialReservation> ApprovedReservationForAsync(MaterialLedgerDbContext db, long itemId, int userId, ReservationStatus status = ReservationStatus.Approved)
    {
        var reservation = new MaterialReservation
        {
            InventoryItemId = itemId,
            ReservedQuantity = 1,
            Status = status,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = userId,
            ApprovedAtUtc = DateTime.UtcNow,
            ApprovedBy = userId
        };
        db.MaterialReservations.Add(reservation);
        await db.SaveChangesAsync();
        return reservation;
    }

    private static CloseoutConsumptionRequest RequestFor(long reservationId, byte[] rowVersion, ProductionConsumptionOutcome outcome,
        decimal? width = null, decimal? length = null, long? destinationId = null, string? notes = null) =>
        new(reservationId, rowVersion, outcome, width, length, destinationId, notes);

    [Fact]
    public async Task MarkReservationAwaitingCloseoutAsync_Approved_TransitionsToAwaitingCloseout()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.MarkReservationAwaitingCloseoutAsync(Guid.NewGuid(), reservation.MaterialReservationId, reservation.RowVersion, userId);

        Assert.Equal(ReservationStatus.AwaitingCloseout, result.Status);
    }

    [Fact]
    public async Task MarkReservationAwaitingCloseoutAsync_NotApproved_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId, ReservationStatus.Proposed);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MarkReservationAwaitingCloseoutAsync(Guid.NewGuid(), reservation.MaterialReservationId, reservation.RowVersion, userId));
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_AllConsumed_Bundle_DecrementsQuantityWithoutForcingDepleted()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, lotId) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 3m, lotId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.CloseoutConsumptionAsync(Guid.NewGuid(),
            RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.AllConsumed), userId);

        Assert.Equal(ReservationStatus.ClosedConsumed, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(2m, itemAfter.QuantityOnHand);
        Assert.Equal(InventoryItemStatus.Available, itemAfter.Status);
        var txn = await db.MaterialTransactions.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(MaterialTransactionType.Consumption, txn.TransactionType);
        Assert.Equal(-1m, txn.QuantityDelta);
        Assert.Equal(lotId, txn.SheetLotId);
        var consumption = await db.ProductionConsumptions.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(ProductionConsumptionOutcome.AllConsumed, consumption.Outcome);
        Assert.Equal(txn.MaterialTransactionId, consumption.PrimaryMaterialTransactionId);
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_AllConsumed_Drop_DepletesItemAndClosesReservation()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.CloseoutConsumptionAsync(Guid.NewGuid(),
            RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.AllConsumed), userId);

        Assert.Equal(ReservationStatus.ClosedConsumed, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(0m, itemAfter.QuantityOnHand);
        Assert.Equal(InventoryItemStatus.Depleted, itemAfter.Status);
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_FullSheetReturned_DropReturnsToAvailableWithNoQuantityChange()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.CloseoutConsumptionAsync(Guid.NewGuid(),
            RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.FullSheetReturned), userId);

        Assert.Equal(ReservationStatus.ClosedReturned, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(1m, itemAfter.QuantityOnHand);
        Assert.Equal(InventoryItemStatus.Available, itemAfter.Status);
        var txn = await db.MaterialTransactions.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(MaterialTransactionType.Return, txn.TransactionType);
        Assert.Equal(0m, txn.QuantityDelta);
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_UsableDropCreated_CreatesRemnantInheritingSheetLotAtDestination()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, destinationSlotId, specId, lotId) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 2m, lotId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.CloseoutConsumptionAsync(Guid.NewGuid(),
            RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.UsableDropCreated,
                width: 10m, length: 15m, destinationId: destinationSlotId), userId);

        Assert.Equal(ReservationStatus.ClosedConsumed, result.Status);
        Assert.NotNull(result.RemnantPublicItemCode);
        var sourceAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(1m, sourceAfter.QuantityOnHand);
        var remnant = await db.InventoryItems.SingleAsync(x => x.PublicItemCode == result.RemnantPublicItemCode);
        Assert.Equal(InventoryItemKind.Drop, remnant.ItemKind);
        Assert.Equal(1m, remnant.QuantityOnHand);
        Assert.Equal(10m, remnant.UsableWidthIn);
        Assert.Equal(15m, remnant.UsableLengthIn);
        Assert.Equal(destinationSlotId, remnant.InventoryLocationId);
        Assert.Equal(lotId, remnant.SheetLotId);
        Assert.Equal(InventoryItemStatus.Available, remnant.Status);
        var remnantTxn = await db.MaterialTransactions.SingleAsync(x => x.TransactionType == MaterialTransactionType.RemnantCreated);
        Assert.Equal(remnant.InventoryItemId, remnantTxn.InventoryItemId);
        Assert.Equal(item.InventoryItemId, remnantTxn.RelatedInventoryItemId);
        Assert.Equal(reservation.MaterialReservationId, remnantTxn.MaterialReservationId);
        var consumeTxn = await db.MaterialTransactions.SingleAsync(x => x.TransactionType == MaterialTransactionType.Consumption);
        var consumption = await db.ProductionConsumptions.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(consumeTxn.MaterialTransactionId, consumption.PrimaryMaterialTransactionId);
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_UsableDropCreated_MissingMeasurements_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, destinationSlotId, specId, _) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 2m);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CloseoutConsumptionAsync(Guid.NewGuid(),
                RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.UsableDropCreated,
                    destinationId: destinationSlotId), userId));
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_UsableDropCreated_NonLeafDestination_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewBundle(slotId, specId, userId, 2m);
        db.InventoryItems.Add(item);
        var areaLocation = new InventoryLocation { LocationCode = $"AREA-{Guid.NewGuid():N}"[..30], DisplayName = "Area", LocationType = InventoryLocationType.Area, StorageMode = InventoryStorageMode.Other, IsActive = true };
        db.InventoryLocations.Add(areaLocation);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CloseoutConsumptionAsync(Guid.NewGuid(),
                RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.UsableDropCreated,
                    width: 10m, length: 15m, destinationId: areaLocation.InventoryLocationId), userId));
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_NeedsReview_QuarantinesItemAndOpensException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.CloseoutConsumptionAsync(Guid.NewGuid(),
            RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.NeedsReview, notes: "unsure what happened"), userId);

        Assert.Equal(ReservationStatus.Exception, result.Status);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Quarantined, itemAfter.Status);
        var exception = await db.MaterialExceptions.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(MaterialExceptionType.OutcomeUnknown, exception.ExceptionType);
        Assert.Equal(MaterialExceptionStatus.Open, exception.Status);
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_FromAwaitingCloseoutStatus_Succeeds()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId, ReservationStatus.AwaitingCloseout);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        var result = await service.CloseoutConsumptionAsync(Guid.NewGuid(),
            RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.AllConsumed), userId);

        Assert.Equal(ReservationStatus.ClosedConsumed, result.Status);
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_ReservationProposed_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId, ReservationStatus.Proposed);
        var service = new CloseoutService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CloseoutConsumptionAsync(Guid.NewGuid(),
                RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.AllConsumed), userId));
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_StaleRowVersion_ThrowsConcurrencyException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));
        var staleRowVersion = new byte[8];

        await Assert.ThrowsAsync<ReservationConcurrencyException>(() =>
            service.CloseoutConsumptionAsync(Guid.NewGuid(),
                RequestFor(reservation.MaterialReservationId, staleRowVersion, ProductionConsumptionOutcome.AllConsumed), userId));
    }

    [Fact]
    public async Task CloseoutConsumptionAsync_RetryWithSameCorrelationId_DoesNotDuplicate()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, _, specId, _) = await SeedAsync(db);
        var item = NewDrop(slotId, specId, userId);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = await ApprovedReservationForAsync(db, item.InventoryItemId, userId);
        var service = new CloseoutService(db, new OperationLogGuard(db));
        var correlationId = Guid.NewGuid();
        var request = RequestFor(reservation.MaterialReservationId, reservation.RowVersion, ProductionConsumptionOutcome.AllConsumed);

        var first = await service.CloseoutConsumptionAsync(correlationId, request, userId);
        var second = await service.CloseoutConsumptionAsync(correlationId, request, userId);

        Assert.Equal(first.Status, second.Status);
        var count = await db.ProductionConsumptions.CountAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(1, count);
        var txnCount = await db.MaterialTransactions.CountAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(1, txnCount);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter CloseoutServiceTests`
Expected: FAIL — `CloseoutService` does not exist yet (compile error).

- [ ] **Step 3: Implement `CloseoutService`**

```csharp
// MaterialLedger.Data/Closeout/CloseoutService.cs
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Closeout;

public record CloseoutConsumptionRequest(
    long MaterialReservationId,
    byte[] ExpectedRowVersion,
    ProductionConsumptionOutcome Outcome,
    decimal? MeasuredUsableWidthIn,
    decimal? MeasuredUsableLengthIn,
    long? DestinationLocationId,
    string? Notes);

public class CloseoutResult
{
    public required long MaterialReservationId { get; init; }
    public required ReservationStatus Status { get; init; }
    public string? RemnantPublicItemCode { get; init; }
}

public class CloseoutService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    private static readonly ReservationStatus[] CloseableStatuses =
    [
        ReservationStatus.Approved, ReservationStatus.AwaitingCloseout
    ];

    private static readonly InventoryLocationType[] LeafLocationTypes =
    [
        InventoryLocationType.Shelf, InventoryLocationType.Bay, InventoryLocationType.Staging,
        InventoryLocationType.Quarantine, InventoryLocationType.Scrap
    ];

    public async Task<ReservationResult> MarkReservationAwaitingCloseoutAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int actorId)
    {
        var payloadHash = HashPayload(new { materialReservationId, expectedRowVersion = Convert.ToHexString(expectedRowVersion) });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "MarkReservationAwaitingCloseout", payloadHash);
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

        if (reservation.Status != ReservationStatus.Approved)
            throw new InvalidOperationException(
                $"Reservation {materialReservationId} is {reservation.Status}, not Approved — only an Approved reservation can be marked awaiting closeout.");

        reservation.Status = ReservationStatus.AwaitingCloseout;

        var result = new ReservationResult { MaterialReservationId = reservation.MaterialReservationId, Status = reservation.Status };
        operationLog.Record(correlationId, "MarkReservationAwaitingCloseout", payloadHash, JsonSerializer.Serialize(result), actorId);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    public async Task<CloseoutResult> CloseoutConsumptionAsync(Guid correlationId, CloseoutConsumptionRequest request, int confirmedBy)
    {
        ValidateRequestShape(request);
        var payloadHash = HashPayload(request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "CloseoutConsumption", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<CloseoutResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var reservation = await db.MaterialReservations.FirstOrDefaultAsync(x => x.MaterialReservationId == request.MaterialReservationId)
            ?? throw new InvalidOperationException($"Reservation {request.MaterialReservationId} does not exist.");

        if (!reservation.RowVersion.SequenceEqual(request.ExpectedRowVersion))
            throw new ReservationConcurrencyException(
                $"Reservation {request.MaterialReservationId} was changed by someone else since it was loaded. Refresh and try again.");

        if (!CloseableStatuses.Contains(reservation.Status))
            throw new InvalidOperationException(
                $"Reservation {request.MaterialReservationId} is {reservation.Status} — only an Approved or AwaitingCloseout reservation can be closed out.");

        var item = await db.InventoryItems.FirstAsync(x => x.InventoryItemId == reservation.InventoryItemId);
        var now = DateTime.UtcNow;
        string? remnantCode = null;

        switch (request.Outcome)
        {
            case ProductionConsumptionOutcome.AllConsumed:
            {
                var isIndividuallyTracked = item.ItemKind is InventoryItemKind.Drop or InventoryItemKind.FullSheet;
                item.QuantityOnHand -= 1;
                if (isIndividuallyTracked)
                    item.Status = InventoryItemStatus.Depleted;

                var txn = NewTransaction(MaterialTransactionType.Consumption, item, reservation.MaterialReservationId, -1m, now, confirmedBy, request.Notes);
                db.MaterialTransactions.Add(txn);
                await db.SaveChangesAsync();

                db.ProductionConsumptions.Add(NewProductionConsumption(reservation.MaterialReservationId, txn.MaterialTransactionId, request.Outcome, 1m, now, confirmedBy, request.Notes));
                reservation.Status = ReservationStatus.ClosedConsumed;
                break;
            }
            case ProductionConsumptionOutcome.FullSheetReturned:
            {
                var isIndividuallyTracked = item.ItemKind is InventoryItemKind.Drop or InventoryItemKind.FullSheet;
                if (isIndividuallyTracked)
                    item.Status = InventoryItemStatus.Available;
                item.LastObservedAtUtc = now;
                item.LastObservedBy = confirmedBy;

                var txn = NewTransaction(MaterialTransactionType.Return, item, reservation.MaterialReservationId, 0m, now, confirmedBy, request.Notes);
                db.MaterialTransactions.Add(txn);
                await db.SaveChangesAsync();

                db.ProductionConsumptions.Add(NewProductionConsumption(reservation.MaterialReservationId, txn.MaterialTransactionId, request.Outcome, 0m, now, confirmedBy, request.Notes));
                reservation.Status = ReservationStatus.ClosedReturned;
                break;
            }
            case ProductionConsumptionOutcome.UsableDropCreated:
            {
                var destination = await db.InventoryLocations.FirstOrDefaultAsync(x => x.InventoryLocationId == request.DestinationLocationId)
                    ?? throw new InvalidOperationException($"Destination location {request.DestinationLocationId} does not exist.");
                if (!LeafLocationTypes.Contains(destination.LocationType))
                    throw new InvalidOperationException(
                        $"Destination location '{destination.LocationCode}' is a {destination.LocationType} — a drop can only be placed at a leaf location.");

                var isIndividuallyTracked = item.ItemKind is InventoryItemKind.Drop or InventoryItemKind.FullSheet;
                item.QuantityOnHand -= 1;
                if (isIndividuallyTracked)
                    item.Status = InventoryItemStatus.Depleted;

                var consumeTxn = NewTransaction(MaterialTransactionType.Consumption, item, reservation.MaterialReservationId, -1m, now, confirmedBy, request.Notes);
                db.MaterialTransactions.Add(consumeTxn);
                await db.SaveChangesAsync();

                long nextSeq;
                await using (var command = db.Database.GetDbConnection().CreateCommand())
                {
                    command.CommandText = "SELECT NEXT VALUE FOR material.ItemCodeSeq";
                    command.Transaction = (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
                    nextSeq = (long)(await command.ExecuteScalarAsync())!;
                }
                remnantCode = $"DROP-{nextSeq}";
                var remnant = new InventoryItem
                {
                    PublicItemCode = remnantCode,
                    MaterialSpecId = item.MaterialSpecId,
                    SheetLotId = item.SheetLotId,
                    InventoryLocationId = destination.InventoryLocationId,
                    ItemKind = InventoryItemKind.Drop,
                    Status = InventoryItemStatus.Available,
                    QuantityOnHand = 1,
                    UsableWidthIn = request.MeasuredUsableWidthIn,
                    UsableLengthIn = request.MeasuredUsableLengthIn,
                    ShapeClassification = ShapeClassification.Rectangular,
                    LastObservedAtUtc = now,
                    LastObservedBy = confirmedBy,
                    Notes = request.Notes
                };
                db.InventoryItems.Add(remnant);
                await db.SaveChangesAsync();

                db.MaterialTransactions.Add(new MaterialTransaction
                {
                    OccurredAtUtc = now,
                    RecordedAtUtc = now,
                    RecordedBy = confirmedBy,
                    TransactionType = MaterialTransactionType.RemnantCreated,
                    InventoryItemId = remnant.InventoryItemId,
                    RelatedInventoryItemId = item.InventoryItemId,
                    MaterialReservationId = reservation.MaterialReservationId,
                    SheetLotId = item.SheetLotId,
                    QuantityDelta = 1m,
                    GeometryWidthInSnapshot = request.MeasuredUsableWidthIn,
                    GeometryLengthInSnapshot = request.MeasuredUsableLengthIn,
                    Notes = request.Notes,
                    SourceSystem = MaterialTransactionSourceSystem.OperatorUI
                });
                await db.SaveChangesAsync();

                db.ProductionConsumptions.Add(NewProductionConsumption(reservation.MaterialReservationId, consumeTxn.MaterialTransactionId, request.Outcome, 1m, now, confirmedBy, request.Notes));
                reservation.Status = ReservationStatus.ClosedConsumed;
                break;
            }
            case ProductionConsumptionOutcome.NeedsReview:
            {
                item.Status = InventoryItemStatus.Quarantined;

                var txn = NewTransaction(MaterialTransactionType.Quarantine, item, reservation.MaterialReservationId, 0m, now, confirmedBy, request.Notes);
                db.MaterialTransactions.Add(txn);
                await db.SaveChangesAsync();

                db.ProductionConsumptions.Add(NewProductionConsumption(reservation.MaterialReservationId, txn.MaterialTransactionId, request.Outcome, null, now, confirmedBy, request.Notes));
                reservation.Status = ReservationStatus.Exception;

                var dedupeKey = $"OutcomeUnknown:Reservation:{reservation.MaterialReservationId}";
                var alreadyOpen = await db.MaterialExceptions.AnyAsync(x => x.DedupeKey == dedupeKey && x.Status == MaterialExceptionStatus.Open);
                if (!alreadyOpen)
                {
                    db.MaterialExceptions.Add(new MaterialException
                    {
                        ExceptionType = MaterialExceptionType.OutcomeUnknown,
                        InventoryItemId = item.InventoryItemId,
                        MaterialReservationId = reservation.MaterialReservationId,
                        DedupeKey = dedupeKey,
                        Status = MaterialExceptionStatus.Open,
                        OpenedAtUtc = now,
                        OpenedBy = confirmedBy,
                        Notes = request.Notes
                    });
                }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(request), $"Unknown outcome {request.Outcome}.");
        }

        var result = new CloseoutResult { MaterialReservationId = reservation.MaterialReservationId, Status = reservation.Status, RemnantPublicItemCode = remnantCode };
        operationLog.Record(correlationId, "CloseoutConsumption", payloadHash, JsonSerializer.Serialize(result), confirmedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static MaterialTransaction NewTransaction(MaterialTransactionType type, InventoryItem item, long reservationId, decimal quantityDelta, DateTime now, int actorId, string? notes) => new()
    {
        OccurredAtUtc = now,
        RecordedAtUtc = now,
        RecordedBy = actorId,
        TransactionType = type,
        InventoryItemId = item.InventoryItemId,
        MaterialReservationId = reservationId,
        SheetLotId = item.SheetLotId,
        QuantityDelta = quantityDelta,
        Notes = notes,
        SourceSystem = MaterialTransactionSourceSystem.OperatorUI
    };

    private static ProductionConsumption NewProductionConsumption(long reservationId, long primaryTransactionId, ProductionConsumptionOutcome outcome, decimal? actualConsumedQuantity, DateTime now, int actorId, string? notes) => new()
    {
        MaterialReservationId = reservationId,
        PrimaryMaterialTransactionId = primaryTransactionId,
        ConfirmedAtUtc = now,
        ConfirmedBy = actorId,
        Outcome = outcome,
        ActualConsumedQuantity = actualConsumedQuantity,
        Notes = notes
    };

    private static void ValidateRequestShape(CloseoutConsumptionRequest request)
    {
        if (request.Outcome != ProductionConsumptionOutcome.UsableDropCreated)
            return;
        if (request.DestinationLocationId is null)
            throw new ArgumentException("A destination slot is required when recording a usable drop.", nameof(request));
        if (request.MeasuredUsableWidthIn is null or <= 0 || request.MeasuredUsableLengthIn is null or <= 0)
            throw new ArgumentException("A usable drop requires a measured width and length greater than zero.", nameof(request));
    }

    private static string HashPayload<T>(T payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
}
```

- [ ] **Step 4: Wire `CloseoutService` into `Program.cs`**

```csharp
// MaterialLedger.Web/Program.cs — add after the existing ReservationService registration
builder.Services.AddScoped(sp => new MaterialLedger.Data.Closeout.CloseoutService(
    sp.GetRequiredService<MaterialLedger.Data.MaterialLedgerDbContext>(),
    sp.GetRequiredService<MaterialLedger.Data.Idempotency.OperationLogGuard>()));
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter CloseoutServiceTests`
Expected: PASS (13 tests)

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Data/Closeout/CloseoutService.cs MaterialLedger.Web/Program.cs MaterialLedger.Tests/CloseoutServiceTests.cs
git commit -m "feat: add CloseoutService implementing MarkReservationAwaitingCloseout and the four CloseoutConsumption outcomes"
```

---

### Task 2: `MaterialExceptionService` — exception scan, listing, and `ResolveMaterialException`

**Files:**
- Create: `MaterialLedger.Data/Exceptions/MaterialExceptionService.cs`
- Modify: `MaterialLedger.Web/Program.cs`
- Modify: `MaterialLedger.Web/appsettings.json`
- Test: `MaterialLedger.Tests/MaterialExceptionServiceTests.cs`

**Interfaces:**
- Consumes: `MaterialLedgerDbContext`, `OperationLogGuard` (existing), `MaterialLedger.Data.Inventory.MaterialFreshness.IsStale` (Phase 2).
- Produces: `ExceptionScanResult(int OpenedCount)`, `MaterialExceptionService.ScanAsync()`, `MaterialExceptionService.GetOpenExceptionsAsync()`, `MaterialExceptionService.GetRecentlyResolvedAsync(int take = 50)`, `MaterialExceptionService.ResolveMaterialExceptionAsync(Guid correlationId, long materialExceptionId, string? resolutionNotes, int resolvedBy)` — used by Task 4's Exceptions page.

- [ ] **Step 1: Write the failing tests**

```csharp
// MaterialLedger.Tests/MaterialExceptionServiceTests.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Exceptions;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class MaterialExceptionServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public MaterialExceptionServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long slotId, long specId)> SeedAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
        var slot = new InventoryLocation { LocationCode = $"SLOT-{Guid.NewGuid():N}"[..30], DisplayName = "Slot", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        var guid = Guid.NewGuid();
        var spec = new MaterialSpec { MaterialFamily = $"Carbon Steel-{guid}"[..30], Grade = $"A36-{guid}"[..30], ThicknessIn = 0.25m, DisplayName = "A36 1/4in", IsActive = true };
        db.AddRange(user, slot, spec);
        await db.SaveChangesAsync();
        return (user.AppUserId, slot.InventoryLocationId, spec.MaterialSpecId);
    }

    private static InventoryItem NewItem(long slotId, long specId, int observedBy, InventoryItemStatus status, DateTime? lastObservedAtUtc = null) => new()
    {
        PublicItemCode = $"SHEET-{Guid.NewGuid():N}"[..20],
        MaterialSpecId = specId,
        InventoryLocationId = slotId,
        ItemKind = InventoryItemKind.FullSheetBundle,
        Status = status,
        QuantityOnHand = 3m,
        ShapeClassification = ShapeClassification.Rectangular,
        LastObservedAtUtc = lastObservedAtUtc ?? DateTime.UtcNow,
        LastObservedBy = observedBy
    };

    [Fact]
    public async Task ScanAsync_QuarantinedItem_OpensQuarantinedMaterialException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);

        var result = await service.ScanAsync();

        Assert.Equal(1, result.OpenedCount);
        var exception = await db.MaterialExceptions.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(MaterialExceptionType.QuarantinedMaterial, exception.ExceptionType);
        Assert.Equal(MaterialExceptionStatus.Open, exception.Status);
    }

    [Fact]
    public async Task ScanAsync_StaleItem_OpensStaleObservationException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Available, DateTime.UtcNow.AddDays(-91));
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);

        await service.ScanAsync();

        var exception = await db.MaterialExceptions.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(MaterialExceptionType.StaleObservation, exception.ExceptionType);
    }

    [Fact]
    public async Task ScanAsync_OverdueAwaitingCloseoutReservation_OpensAwaitingCloseoutOverdueException()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Available);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var reservation = new MaterialReservation
        {
            InventoryItemId = item.InventoryItemId,
            ReservedQuantity = 1,
            Status = ReservationStatus.AwaitingCloseout,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-3),
            CreatedBy = userId,
            ApprovedAtUtc = DateTime.UtcNow.AddDays(-3),
            ApprovedBy = userId
        };
        db.MaterialReservations.Add(reservation);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);

        await service.ScanAsync();

        var exception = await db.MaterialExceptions.SingleAsync(x => x.MaterialReservationId == reservation.MaterialReservationId);
        Assert.Equal(MaterialExceptionType.AwaitingCloseoutOverdue, exception.ExceptionType);
    }

    [Fact]
    public async Task ScanAsync_ConditionAlreadyOpen_DoesNotOpenSecondRow()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);

        await service.ScanAsync();
        var secondScan = await service.ScanAsync();

        Assert.Equal(0, secondScan.OpenedCount);
        var count = await db.MaterialExceptions.CountAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ScanAsync_ConditionRecursAfterResolution_OpensNewRowPreservingOld()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);
        await service.ScanAsync();
        var firstException = await db.MaterialExceptions.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        await service.ResolveMaterialExceptionAsync(Guid.NewGuid(), firstException.MaterialExceptionId, "reviewed", userId);

        var secondScan = await service.ScanAsync();

        Assert.Equal(1, secondScan.OpenedCount);
        var allExceptions = await db.MaterialExceptions.Where(x => x.InventoryItemId == item.InventoryItemId).ToListAsync();
        Assert.Equal(2, allExceptions.Count);
        Assert.Single(allExceptions, x => x.Status == MaterialExceptionStatus.Fixed);
        Assert.Single(allExceptions, x => x.Status == MaterialExceptionStatus.Open);
    }

    [Fact]
    public async Task ResolveMaterialExceptionAsync_OpenException_MarksFixedWithActorAndNotes()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);
        await service.ScanAsync();
        var exception = await db.MaterialExceptions.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);

        var resolved = await service.ResolveMaterialExceptionAsync(Guid.NewGuid(), exception.MaterialExceptionId, "operator re-identified the material", userId);

        Assert.Equal(MaterialExceptionStatus.Fixed, resolved.Status);
        Assert.Equal(userId, resolved.ResolvedBy);
        Assert.Equal("operator re-identified the material", resolved.ResolutionNotes);
        Assert.NotNull(resolved.ResolvedAtUtc);
        var itemAfter = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Quarantined, itemAfter.Status); // resolving does not auto-revert item state
    }

    [Fact]
    public async Task ResolveMaterialExceptionAsync_AlreadyResolved_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);
        await service.ScanAsync();
        var exception = await db.MaterialExceptions.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        await service.ResolveMaterialExceptionAsync(Guid.NewGuid(), exception.MaterialExceptionId, "first resolution", userId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResolveMaterialExceptionAsync(Guid.NewGuid(), exception.MaterialExceptionId, "second resolution", userId));
    }

    [Fact]
    public async Task GetOpenExceptionsAsync_ReturnsOnlyOpenRows()
    {
        await using var db = _fixture.CreateContext();
        var (userId, slotId, specId) = await SeedAsync(db);
        var item = NewItem(slotId, specId, userId, InventoryItemStatus.Quarantined);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        var service = new MaterialExceptionService(db, new OperationLogGuard(db), freshnessThresholdDays: 90, closeoutOverdueThresholdDays: 1);
        await service.ScanAsync();
        var exception = await db.MaterialExceptions.SingleAsync(x => x.InventoryItemId == item.InventoryItemId);
        await service.ResolveMaterialExceptionAsync(Guid.NewGuid(), exception.MaterialExceptionId, "resolved", userId);

        var open = await service.GetOpenExceptionsAsync();
        var resolved = await service.GetRecentlyResolvedAsync();

        Assert.DoesNotContain(open, x => x.MaterialExceptionId == exception.MaterialExceptionId);
        Assert.Contains(resolved, x => x.MaterialExceptionId == exception.MaterialExceptionId);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter MaterialExceptionServiceTests`
Expected: FAIL — `MaterialExceptionService` does not exist yet (compile error).

- [ ] **Step 3: Implement `MaterialExceptionService`**

```csharp
// MaterialLedger.Data/Exceptions/MaterialExceptionService.cs
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Exceptions;

public record ExceptionScanResult(int OpenedCount);

public class MaterialExceptionService(MaterialLedgerDbContext db, OperationLogGuard operationLog, int freshnessThresholdDays, int closeoutOverdueThresholdDays)
{
    private static readonly ReservationStatus[] OverdueEligibleStatuses =
    [
        ReservationStatus.Approved, ReservationStatus.AwaitingCloseout
    ];

    public async Task<ExceptionScanResult> ScanAsync()
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var openDedupeKeys = (await db.MaterialExceptions
            .Where(x => x.Status == MaterialExceptionStatus.Open)
            .Select(x => x.DedupeKey)
            .ToListAsync()).ToHashSet();

        var now = DateTime.UtcNow;
        var candidates = new List<MaterialException>();

        var quarantinedItems = await db.InventoryItems
            .Where(x => x.Status == InventoryItemStatus.Quarantined)
            .ToListAsync();
        foreach (var item in quarantinedItems)
        {
            var key = $"QuarantinedMaterial:Item:{item.InventoryItemId}";
            if (openDedupeKeys.Add(key))
                candidates.Add(new MaterialException
                {
                    ExceptionType = MaterialExceptionType.QuarantinedMaterial,
                    InventoryItemId = item.InventoryItemId,
                    DedupeKey = key,
                    Status = MaterialExceptionStatus.Open,
                    OpenedAtUtc = now
                });
        }

        var activeItems = await db.InventoryItems
            .Where(x => x.Status != InventoryItemStatus.Depleted && x.Status != InventoryItemStatus.Scrapped)
            .ToListAsync();
        foreach (var item in activeItems.Where(x => MaterialFreshness.IsStale(x.LastObservedAtUtc, freshnessThresholdDays)))
        {
            var key = $"StaleObservation:Item:{item.InventoryItemId}";
            if (openDedupeKeys.Add(key))
                candidates.Add(new MaterialException
                {
                    ExceptionType = MaterialExceptionType.StaleObservation,
                    InventoryItemId = item.InventoryItemId,
                    DedupeKey = key,
                    Status = MaterialExceptionStatus.Open,
                    OpenedAtUtc = now
                });
        }

        var candidateReservations = await db.MaterialReservations
            .Where(x => OverdueEligibleStatuses.Contains(x.Status) && x.ApprovedAtUtc != null)
            .ToListAsync();
        foreach (var reservation in candidateReservations.Where(x => (now - x.ApprovedAtUtc!.Value).TotalDays > closeoutOverdueThresholdDays))
        {
            var key = $"AwaitingCloseoutOverdue:Reservation:{reservation.MaterialReservationId}";
            if (openDedupeKeys.Add(key))
                candidates.Add(new MaterialException
                {
                    ExceptionType = MaterialExceptionType.AwaitingCloseoutOverdue,
                    MaterialReservationId = reservation.MaterialReservationId,
                    DedupeKey = key,
                    Status = MaterialExceptionStatus.Open,
                    OpenedAtUtc = now
                });
        }

        if (candidates.Count > 0)
        {
            db.MaterialExceptions.AddRange(candidates);
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync();
        return new ExceptionScanResult(candidates.Count);
    }

    public Task<List<MaterialException>> GetOpenExceptionsAsync() =>
        db.MaterialExceptions
            .Where(x => x.Status == MaterialExceptionStatus.Open)
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Include(x => x.MaterialReservation)
            .OrderByDescending(x => x.OpenedAtUtc)
            .ToListAsync();

    public Task<List<MaterialException>> GetRecentlyResolvedAsync(int take = 50) =>
        db.MaterialExceptions
            .Where(x => x.Status == MaterialExceptionStatus.Fixed)
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Include(x => x.MaterialReservation)
            .Include(x => x.ResolvedByUser)
            .OrderByDescending(x => x.ResolvedAtUtc)
            .Take(take)
            .ToListAsync();

    public async Task<MaterialException> ResolveMaterialExceptionAsync(Guid correlationId, long materialExceptionId, string? resolutionNotes, int resolvedBy)
    {
        var payloadHash = HashPayload(new { materialExceptionId, resolutionNotes });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "ResolveMaterialException", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replayId = JsonSerializer.Deserialize<long>(outcome.ResultSummary!);
            await transaction.CommitAsync();
            return (await db.MaterialExceptions.FindAsync(replayId))!;
        }

        var exception = await db.MaterialExceptions.FirstOrDefaultAsync(x => x.MaterialExceptionId == materialExceptionId)
            ?? throw new InvalidOperationException($"Exception {materialExceptionId} does not exist.");

        if (exception.Status != MaterialExceptionStatus.Open)
            throw new InvalidOperationException($"Exception {materialExceptionId} is already {exception.Status}.");

        exception.Status = MaterialExceptionStatus.Fixed;
        exception.ResolvedAtUtc = DateTime.UtcNow;
        exception.ResolvedBy = resolvedBy;
        exception.ResolutionNotes = resolutionNotes;

        operationLog.Record(correlationId, "ResolveMaterialException", payloadHash, JsonSerializer.Serialize(exception.MaterialExceptionId), resolvedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return exception;
    }

    private static string HashPayload<T>(T payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
}
```

- [ ] **Step 4: Add the overdue-threshold setting and wire the service into `Program.cs`**

```json
// MaterialLedger.Web/appsettings.json — add alongside ReservationFreshnessThresholdDays
{
  "ConnectionStrings": {
    "MaterialLedger": ""
  },
  "ReservationFreshnessThresholdDays": 90,
  "CloseoutOverdueThresholdDays": 1,
  "SeedAccounts": [
    { "Username": "programmer", "DisplayName": "Programmer", "Role": "Programmer", "InitialPassword": "" },
    { "Username": "operator", "DisplayName": "Operator", "Role": "Operator", "InitialPassword": "" },
    { "Username": "manager", "DisplayName": "Manager", "Role": "Manager", "InitialPassword": "" }
  ]
}
```

```csharp
// MaterialLedger.Web/Program.cs — add after the existing CloseoutService registration
builder.Services.AddScoped(sp => new MaterialLedger.Data.Exceptions.MaterialExceptionService(
    sp.GetRequiredService<MaterialLedger.Data.MaterialLedgerDbContext>(),
    sp.GetRequiredService<MaterialLedger.Data.Idempotency.OperationLogGuard>(),
    builder.Configuration.GetValue("ReservationFreshnessThresholdDays", 90),
    builder.Configuration.GetValue("CloseoutOverdueThresholdDays", 1)));
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter MaterialExceptionServiceTests`
Expected: PASS (8 tests)

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Data/Exceptions/MaterialExceptionService.cs MaterialLedger.Web/Program.cs MaterialLedger.Web/appsettings.json MaterialLedger.Tests/MaterialExceptionServiceTests.cs
git commit -m "feat: add MaterialExceptionService with dedupe-keyed scan, ResolveMaterialException, and open/resolved listings"
```

---

### Task 3: `Pages/Closeout` — reservation list, closeout detail with four outcomes, success page

**Files:**
- Create: `MaterialLedger.Web/Pages/Closeout/Index.cshtml`, `Index.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/Closeout/Details.cshtml`, `Details.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/Closeout/Success.cshtml`, `Success.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Index.cshtml`

**Interfaces:**
- Consumes: `CloseoutService.MarkReservationAwaitingCloseoutAsync`, `CloseoutService.CloseoutConsumptionAsync`, `CloseoutConsumptionRequest`, `CloseoutResult` (Task 1).

- [ ] **Step 1: Reservation list with "Start job" and "Close material" actions**

```csharp
// MaterialLedger.Web/Pages/Closeout/Index.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Closeout;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Closeout;

[Authorize(Policy = MaterialLedgerPolicies.RequireOperatorOrManager)]
public class IndexModel(MaterialLedgerDbContext db, CloseoutService closeout) : PageModel
{
    public List<MaterialReservation> Reservations { get; set; } = [];
    public Dictionary<long, Guid> CorrelationIdsByReservationId { get; set; } = [];
    public string? Error { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostStartJobAsync(long id, Guid correlationId, string expectedRowVersionBase64)
    {
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await closeout.MarkReservationAwaitingCloseoutAsync(correlationId, id, Convert.FromBase64String(expectedRowVersionBase64), userId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ReservationConcurrencyException or OperationConflictException or DbUpdateException)
        {
            Error = ex is DbUpdateException ? "This reservation was changed by someone else — refresh and try again." : ex.Message;
            await LoadAsync();
            return Page();
        }

        return RedirectToPage("Index");
    }

    private async Task LoadAsync()
    {
        Reservations = await db.MaterialReservations
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Where(x => x.Status == ReservationStatus.Approved || x.Status == ReservationStatus.AwaitingCloseout)
            .OrderByDescending(x => x.ApprovedAtUtc)
            .ToListAsync();
        CorrelationIdsByReservationId = Reservations.ToDictionary(x => x.MaterialReservationId, _ => Guid.NewGuid());
    }
}
```

```html
@* MaterialLedger.Web/Pages/Closeout/Index.cshtml *@
@page
@model MaterialLedger.Web.Pages.Closeout.IndexModel
@using MaterialLedger.Data.Models
@{ Layout = "Shared/_Layout"; }
<h1>Closeout</h1>
@if (Model.Error is not null)
{
    <p style="color:red">@Model.Error</p>
}
<table>
    <thead><tr><th>Item</th><th>Material</th><th>Job/Drawing</th><th>Equipment</th><th>Status</th><th></th></tr></thead>
    <tbody>
    @foreach (var r in Model.Reservations)
    {
        <tr>
            <td>@r.InventoryItem?.PublicItemCode</td>
            <td>@r.InventoryItem?.MaterialSpec?.DisplayName</td>
            <td>@r.ShopOpsJobId @r.DrawingId @r.DrawingRevision</td>
            <td>@r.Equipment</td>
            <td>@r.Status</td>
            <td style="white-space:nowrap">
            @if (r.Status == ReservationStatus.Approved)
            {
                <form method="post" asp-page-handler="StartJob" asp-route-id="@r.MaterialReservationId" style="display:inline">
                    @Html.AntiForgeryToken()
                    <input type="hidden" name="correlationId" value="@Model.CorrelationIdsByReservationId[r.MaterialReservationId]" />
                    <input type="hidden" name="expectedRowVersionBase64" value="@Convert.ToBase64String(r.RowVersion)" />
                    <button type="submit" style="font-size:1.1em; padding:0.6em 1em;">Start job</button>
                </form>
            }
            <a asp-page="Details" asp-route-id="@r.MaterialReservationId"><button type="button" style="font-size:1.1em; padding:0.6em 1em;">Close material</button></a>
            </td>
        </tr>
    }
    </tbody>
</table>
```

- [ ] **Step 2: Closeout detail with the four outcome actions**

```csharp
// MaterialLedger.Web/Pages/Closeout/Details.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Closeout;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Closeout;

[Authorize(Policy = MaterialLedgerPolicies.RequireOperatorOrManager)]
public class DetailsModel(MaterialLedgerDbContext db, CloseoutService closeout) : PageModel
{
    private static readonly InventoryLocationType[] DestinationChoiceTypes =
    [
        InventoryLocationType.Shelf, InventoryLocationType.Bay, InventoryLocationType.Staging
    ];

    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public string ExpectedRowVersionBase64 { get; set; } = string.Empty;
    [BindProperty] public string? Notes { get; set; }
    [BindProperty] public decimal? MeasuredUsableWidthIn { get; set; }
    [BindProperty] public decimal? MeasuredUsableLengthIn { get; set; }
    [BindProperty] public long? DestinationLocationId { get; set; }

    public MaterialReservation? Reservation { get; set; }
    public List<InventoryLocation> DestinationChoices { get; set; } = [];
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        Reservation = await LoadAsync(id);
        if (Reservation is null)
            return NotFound();
        DestinationChoices = await db.InventoryLocations
            .Where(x => x.IsActive && DestinationChoiceTypes.Contains(x.LocationType))
            .OrderBy(x => x.LocationCode)
            .ToListAsync();
        CorrelationId = Guid.NewGuid();
        return Page();
    }

    public Task<IActionResult> OnPostAllConsumedAsync(long id) =>
        SubmitAsync(id, ProductionConsumptionOutcome.AllConsumed);

    public Task<IActionResult> OnPostFullSheetReturnedAsync(long id) =>
        SubmitAsync(id, ProductionConsumptionOutcome.FullSheetReturned);

    public Task<IActionResult> OnPostUsableDropCreatedAsync(long id) =>
        SubmitAsync(id, ProductionConsumptionOutcome.UsableDropCreated);

    public Task<IActionResult> OnPostNeedsReviewAsync(long id) =>
        SubmitAsync(id, ProductionConsumptionOutcome.NeedsReview);

    private async Task<IActionResult> SubmitAsync(long id, ProductionConsumptionOutcome outcome)
    {
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new CloseoutConsumptionRequest(id, Convert.FromBase64String(ExpectedRowVersionBase64), outcome,
                MeasuredUsableWidthIn, MeasuredUsableLengthIn, DestinationLocationId, Notes);
            var result = await closeout.CloseoutConsumptionAsync(CorrelationId, request, userId);
            return RedirectToPage("Success", new { id, remnantCode = result.RemnantPublicItemCode });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or ReservationConcurrencyException or OperationConflictException or DbUpdateException)
        {
            Reservation = await LoadAsync(id);
            DestinationChoices = await db.InventoryLocations
                .Where(x => x.IsActive && DestinationChoiceTypes.Contains(x.LocationType))
                .OrderBy(x => x.LocationCode)
                .ToListAsync();
            CorrelationId = Guid.NewGuid();
            Error = ex is DbUpdateException ? "This reservation was changed by someone else — refresh and try again." : ex.Message;
            return Page();
        }
    }

    private Task<MaterialReservation?> LoadAsync(long id) =>
        db.MaterialReservations
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.InventoryLocation)
            .FirstOrDefaultAsync(x => x.MaterialReservationId == id);
}
```

```html
@* MaterialLedger.Web/Pages/Closeout/Details.cshtml *@
@page "{id:long}"
@model MaterialLedger.Web.Pages.Closeout.DetailsModel
@{ Layout = "Shared/_Layout"; }
<h1>Close material — reservation @Model.Reservation!.MaterialReservationId</h1>
@if (Model.Error is not null)
{
    <p style="color:red">@Model.Error</p>
}
<dl>
    <dt>Item</dt><dd>@Model.Reservation.InventoryItem?.PublicItemCode — @Model.Reservation.InventoryItem?.MaterialSpec?.DisplayName — @Model.Reservation.InventoryItem?.InventoryLocation?.LocationCode</dd>
    <dt>Job / Drawing</dt><dd>@Model.Reservation.ShopOpsJobId @Model.Reservation.DrawingId @Model.Reservation.DrawingRevision</dd>
    <dt>Equipment</dt><dd>@Model.Reservation.Equipment</dd>
    <dt>Expected remainder</dt><dd>@(Model.Reservation.ExpectedRemainderWidthIn is not null ? $"{Model.Reservation.ExpectedRemainderWidthIn} x {Model.Reservation.ExpectedRemainderLengthIn} ({Model.Reservation.ExpectedRemainderSourceType})" : "-")</dd>
    <dt>Status</dt><dd>@Model.Reservation.Status</dd>
</dl>

<form method="post" asp-page-handler="AllConsumed" asp-route-id="@Model.Reservation.MaterialReservationId">
    @Html.AntiForgeryToken()
    <input type="hidden" asp-for="CorrelationId" />
    <input type="hidden" name="ExpectedRowVersionBase64" value="@Convert.ToBase64String(Model.Reservation.RowVersion)" />
    <button type="submit" style="font-size:1.4em; padding:0.8em 1.2em; margin:0.4em;">All consumed / scrap remaining</button>
</form>

<form method="post" asp-page-handler="FullSheetReturned" asp-route-id="@Model.Reservation.MaterialReservationId">
    @Html.AntiForgeryToken()
    <input type="hidden" asp-for="CorrelationId" />
    <input type="hidden" name="ExpectedRowVersionBase64" value="@Convert.ToBase64String(Model.Reservation.RowVersion)" />
    <button type="submit" style="font-size:1.4em; padding:0.8em 1.2em; margin:0.4em;">Full sheet returned</button>
</form>

<form method="post" asp-page-handler="UsableDropCreated" asp-route-id="@Model.Reservation.MaterialReservationId">
    @Html.AntiForgeryToken()
    <input type="hidden" asp-for="CorrelationId" />
    <input type="hidden" name="ExpectedRowVersionBase64" value="@Convert.ToBase64String(Model.Reservation.RowVersion)" />
    <fieldset>
        <legend>Usable rectangular drop remains</legend>
        <label>Measured usable width (in) <input asp-for="MeasuredUsableWidthIn" type="number" step="0.001" /></label>
        <label>Measured usable length (in) <input asp-for="MeasuredUsableLengthIn" type="number" step="0.001" /></label>
        <label>Destination slot
            <select asp-for="DestinationLocationId">
                <option value="">-- select --</option>
                @foreach (var loc in Model.DestinationChoices)
                {
                    <option value="@loc.InventoryLocationId">@loc.LocationCode — @loc.DisplayName</option>
                }
            </select>
        </label>
        <button type="submit" style="font-size:1.4em; padding:0.8em 1.2em; margin:0.4em;">Record usable drop</button>
    </fieldset>
</form>

<form method="post" asp-page-handler="NeedsReview" asp-route-id="@Model.Reservation.MaterialReservationId">
    @Html.AntiForgeryToken()
    <input type="hidden" asp-for="CorrelationId" />
    <input type="hidden" name="ExpectedRowVersionBase64" value="@Convert.ToBase64String(Model.Reservation.RowVersion)" />
    <label>Note <input asp-for="Notes" /></label>
    <button type="submit" style="font-size:1.4em; padding:0.8em 1.2em; margin:0.4em;">Needs review / outcome unknown</button>
</form>

<p><a asp-page="Index">Back to closeout list</a></p>
```

- [ ] **Step 3: Success page**

```csharp
// MaterialLedger.Web/Pages/Closeout/Success.cshtml.cs
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Closeout;

[Authorize(Policy = MaterialLedgerPolicies.RequireOperatorOrManager)]
public class SuccessModel : PageModel
{
    public long ReservationId { get; set; }
    public string? RemnantCode { get; set; }

    public void OnGet(long id, string? remnantCode)
    {
        ReservationId = id;
        RemnantCode = remnantCode;
    }
}
```

```html
@* MaterialLedger.Web/Pages/Closeout/Success.cshtml *@
@page
@model MaterialLedger.Web.Pages.Closeout.SuccessModel
@{ Layout = "Shared/_Layout"; }
<h1>Reservation @Model.ReservationId closed out</h1>
@if (Model.RemnantCode is not null)
{
    <p>New drop item code: <strong>@Model.RemnantCode</strong></p>
    <p>QR payload for a printed label: <code>@Model.RemnantCode</code></p>
}
<p><a asp-page="Index">Back to closeout list</a></p>
```

- [ ] **Step 4: Add navigation from the home page**

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
    @if (User.IsInRole("Operator") || User.IsInRole("Manager"))
    {
        <li><a asp-page="/Closeout/Index">Closeout</a></li>
    }
    @if (User.IsInRole("Programmer") || User.IsInRole("Manager"))
    {
        <li><a asp-page="/Census/SelectSlot">Opening census</a></li>
    }
</ul>
```

- [ ] **Step 5: Build and manually verify**

Run: `dotnet build`
Expected: builds with no errors. Once a reachable dev SQL Server connection string is configured, run the app and confirm: (a) an Operator session sees `/Closeout/Index` listing `Approved`/`AwaitingCloseout` reservations with a "Start job" button only on `Approved` rows; (b) `/Closeout/Details/{id}` shows all four outcome buttons outside any collapsed/optional card (spec section 8 acceptance test 9); (c) submitting "Record usable drop" without width/length/destination shows the validation error inline; (d) a successful "All consumed" submission redirects to `/Closeout/Success` and the reservation now shows `ClosedConsumed` on `/Reservations/Details/{id}`; (e) a Programmer-only session visiting `/Closeout/Index` directly is redirected to `/AccessDenied`.

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Web/Pages/Closeout MaterialLedger.Web/Pages/Index.cshtml
git commit -m "feat: add closeout list, four-outcome detail screen, and success page"
```

---

### Task 4: `Pages/Exceptions` — exception queue with resolve action

**Files:**
- Create: `MaterialLedger.Web/Pages/Exceptions/Index.cshtml`, `Index.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Index.cshtml`

**Interfaces:**
- Consumes: `MaterialExceptionService.ScanAsync`, `GetOpenExceptionsAsync`, `GetRecentlyResolvedAsync`, `ResolveMaterialExceptionAsync` (Task 2).

- [ ] **Step 1: Implement the exceptions page**

```csharp
// MaterialLedger.Web/Pages/Exceptions/Index.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data.Exceptions;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Exceptions;

[Authorize]
public class IndexModel(MaterialExceptionService exceptions) : PageModel
{
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public string? ResolutionNotes { get; set; }

    public List<MaterialException> OpenExceptions { get; set; } = [];
    public List<MaterialException> ResolvedExceptions { get; set; } = [];
    public Dictionary<long, Guid> CorrelationIdsByExceptionId { get; set; } = [];
    public bool CanResolve { get; set; }
    public string? Error { get; set; }

    public async Task OnGetAsync()
    {
        await exceptions.ScanAsync();
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostResolveAsync(long id, Guid correlationId)
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager"))
            return Forbid();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await exceptions.ResolveMaterialExceptionAsync(correlationId, id, ResolutionNotes, userId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationConflictException)
        {
            Error = ex.Message;
            await LoadAsync();
            return Page();
        }

        return RedirectToPage("Index");
    }

    private async Task LoadAsync()
    {
        OpenExceptions = await exceptions.GetOpenExceptionsAsync();
        ResolvedExceptions = await exceptions.GetRecentlyResolvedAsync();
        CorrelationIdsByExceptionId = OpenExceptions.ToDictionary(x => x.MaterialExceptionId, _ => Guid.NewGuid());
        CanResolve = User.IsInRole("Programmer") || User.IsInRole("Manager");
    }
}
```

```html
@* MaterialLedger.Web/Pages/Exceptions/Index.cshtml *@
@page
@model MaterialLedger.Web.Pages.Exceptions.IndexModel
@{ Layout = "Shared/_Layout"; }
<h1>Exception queue</h1>
@if (Model.Error is not null)
{
    <p style="color:red">@Model.Error</p>
}
<h2>Open</h2>
<table>
    <thead><tr><th>Type</th><th>Item</th><th>Reservation</th><th>Opened</th><th></th></tr></thead>
    <tbody>
    @foreach (var e in Model.OpenExceptions)
    {
        <tr>
            <td>@e.ExceptionType</td>
            <td>@e.InventoryItem?.PublicItemCode @e.InventoryItem?.MaterialSpec?.DisplayName</td>
            <td>@(e.MaterialReservationId is not null ? $"#{e.MaterialReservationId}" : "-")</td>
            <td>@e.OpenedAtUtc.ToString("u")</td>
            <td>
            @if (Model.CanResolve)
            {
                <form method="post" action="?handler=Resolve" style="display:inline">
                    @Html.AntiForgeryToken()
                    <input type="hidden" name="id" value="@e.MaterialExceptionId" />
                    <input type="hidden" name="CorrelationId" value="@Model.CorrelationIdsByExceptionId[e.MaterialExceptionId]" />
                    <input type="text" name="ResolutionNotes" placeholder="Resolution notes" />
                    <button type="submit">Resolve</button>
                </form>
            }
            </td>
        </tr>
    }
    </tbody>
</table>

<h2>Recently resolved</h2>
<table>
    <thead><tr><th>Type</th><th>Item</th><th>Status</th><th>Resolved</th></tr></thead>
    <tbody>
    @foreach (var e in Model.ResolvedExceptions)
    {
        <tr>
            <td>@e.ExceptionType</td>
            <td>@e.InventoryItem?.PublicItemCode @e.InventoryItem?.MaterialSpec?.DisplayName</td>
            <td><strong>FIXED</strong></td>
            <td>@e.ResolvedAtUtc?.ToString("u") by @e.ResolvedByUser?.DisplayName</td>
        </tr>
    }
    </tbody>
</table>
```

- [ ] **Step 2: Add navigation from the home page**

```html
@* MaterialLedger.Web/Pages/Index.cshtml — add alongside the Closeout link from Task 3 *@
<li><a asp-page="/Exceptions/Index">Exception queue</a></li>
```

- [ ] **Step 3: Build and manually verify**

Run: `dotnet build`
Expected: builds with no errors. Confirm: (a) any signed-in role can view `/Exceptions/Index`; (b) only a Programmer/Manager session sees "Resolve" buttons; (c) resolving an exception moves it from "Open" to "Recently resolved" showing `FIXED` (spec section 8 acceptance test 9); (d) after resolving a `QuarantinedMaterial` exception whose underlying item is still `Quarantined`, reloading the page reopens a new row for the same item (dedupe key recurrence, acceptance test 12) — this is expected per the Global Constraints note, not a bug.

- [ ] **Step 4: Commit**

```bash
git add MaterialLedger.Web/Pages/Exceptions MaterialLedger.Web/Pages/Index.cshtml
git commit -m "feat: add exception queue with on-demand scan and role-gated resolve action"
```

---

### Task 5: Phase 3 ShopOps grants

**Files:**
- Create: `docs/schema/material-permissions-v4.sql`

- [ ] **Step 1: Write the grants script**

```sql
-- docs/schema/material-permissions-v4.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, Phase 3.
--
-- Phase 3 introduces the closeout/exception write surface: CloseoutService
-- (MarkReservationAwaitingCloseoutAsync, CloseoutConsumptionAsync) and MaterialExceptionService
-- (ScanAsync, ResolveMaterialExceptionAsync). Per the note at the top of material-permissions-v1.sql,
-- grants arrive with the code that uses them — this file adds ONLY what Phase 3 needs. It does not
-- touch the Phase 0/1/2 grants already applied by material-permissions-v1.sql/v2.sql/v3.sql, and is
-- safe to re-run (GRANT is idempotent).
--
-- Run against the real `MaterialLedger` database using an account with GRANT rights (sa or a member
-- of db_owner/db_securityadmin), the same way material-permissions-v1.sql/v2.sql/v3.sql were applied.
-- This is a manual administrative step — the running application never applies its own grants.

USE MaterialLedger;
GO

-- ProductionConsumptions: new table for Phase 3. CloseoutConsumption inserts exactly one row per
-- successful closeout (enforced by the existing unique index on MaterialReservationId). No
-- UPDATE/DELETE — a closeout outcome, once recorded, is never edited; a correction would be a new
-- MaterialTransaction/Adjustment, not a mutated ProductionConsumption row.
GRANT SELECT, INSERT ON material.ProductionConsumptions TO materialledger_app;

-- MaterialExceptions: new table for Phase 3. ScanAsync inserts newly-detected exceptions;
-- ResolveMaterialException updates Status/ResolvedAtUtc/ResolvedBy/ResolutionNotes on the same row.
-- No DELETE — a resolved exception stays as history; a recurring condition opens a NEW row rather
-- than reusing the old one (spec section 4E), matching every other table in this schema.
GRANT SELECT, INSERT, UPDATE ON material.MaterialExceptions TO materialledger_app;
GO

-- No new grant is needed on InventoryItems (Phase 1/2 already granted SELECT/INSERT/UPDATE, which
-- covers both quantity/status mutation on existing items AND inserting the new remnant InventoryItem
-- row a UsableDropCreated closeout creates), MaterialReservations (Phase 2 already granted
-- SELECT/INSERT/UPDATE, which covers the Approved -> AwaitingCloseout -> Closed*/Exception
-- transitions), MaterialTransactions (Phase 0 already granted SELECT/INSERT for the
-- Consumption/Return/RemnantCreated/Quarantine transaction types this phase writes), or ItemCodeSeq
-- (Phase 1 already granted UPDATE, reused here to mint the new remnant item's PublicItemCode).
```

- [ ] **Step 2: Commit**

```bash
git add docs/schema/material-permissions-v4.sql
git commit -m "docs: add Phase 3 ShopOps grants for closeout and exception write surfaces"
```

---

### Task 6: Controlled pilot verification (manual, live — spec section 7 Phase 3 item 3)

This task has no code changes. It is the live-verification gate the spec requires before Phase 3 can be
called done (spec section 8, "Live verification before operational use", items 5–7). It cannot be
automated in this repo — it requires REMCO's physical participation and a real Forge SQL Server
connection.

- [ ] **Step 1: Apply `docs/schema/material-permissions-v4.sql` to the real `MaterialLedger` database on forge-sql** (manual, human-run, per the file's own header — do not attempt this from an agent session).

- [ ] **Step 2: Confirm the deployed app is pointed at forge-sql and the Closeout/Exceptions pages render for a real logged-in Operator and Manager account** (spec section 8, live verification item 4, extended to the new pages).

- [ ] **Step 3: Create one real but controlled reservation for the laser, run an actual laser job, and record the correct actual outcome through `/Closeout/Details`.** Repeat for at least one other equipment type actively drawing from this stock (plasma or shear — confirm which with REMCO per spec section 9's open input, "Confirmation of which equipment... actively draws from this shared material stock today"). Cover all four outcomes across the laser + second-equipment pilot set (spec section 7, Phase 3 item 3: "at least the laser and one other equipment type... reconcile expected versus measured outcome for each before calling this phase done").

- [ ] **Step 4: Read back ShopOps ledger rows, transaction rows, `ProductionConsumption` rows, reservation status, and UI exception state for each piloted closeout. Confirm they reconcile without any manual database editing** (spec section 8, live verification item 6).

- [ ] **Step 5: Leave one reservation deliberately open past `CloseoutOverdueThresholdDays` and confirm it surfaces in `/Exceptions/Index` on the next visit rather than silently going stale** (spec section 8, live verification item 7).

- [ ] **Step 6: Record the pilot results (what was run, on which equipment, each outcome exercised, and any reconciliation discrepancies) in `docs/PROGRESS.md`'s session log, and update the Phase 3 row in its status table to Done once all four outcomes have been verified against real equipment.**

---

## Self-review notes

- **Spec coverage:** Section 4C (closeout screen: reservation list, `Start job`, `Close material`, four outcomes, final review tap, printable label on success) → Task 3. Section 4E (exception queue, `FIXED` display, resurfacing) → Task 2 + Task 4. Section 3's `ProductionConsumption` outcome-to-transaction table → Task 1's switch statement, mirrored exactly. Section 3's `MaterialException` dedupe/reopen behavior → Task 2's `ScanAsync`. Section 7 Phase 3 items 1–3 → Tasks 1–4 (item 1), Task 1 (item 2), Task 6 (item 3). Section 8 acceptance tests 4–8, 12 → Task 1/Task 2 automated tests; tests 9–10 (UI structural / API authorization) → Task 3/4 manual-verification steps, matching the existing repo convention (no `WebApplicationFactory`-based tests exist in this codebase for any phase so far — Phase 2 verified authorization manually too).
- **Placeholder scan:** No TBD/TODO markers; every step has concrete, complete code. Task 6 is intentionally a manual checklist (no code) because the spec's own Phase 3 item 3 and section 8's "live verification" items are explicitly physical/live steps, not something to fake with placeholder code.
- **Type consistency:** `CloseoutConsumptionRequest`, `CloseoutResult`, `ReservationResult`, `ReservationConcurrencyException`, `ExceptionScanResult` are defined once (Tasks 1–2) and consumed with matching signatures in Tasks 3–4. `ProductionConsumptionOutcome` and `ReservationStatus` enum members used in Task 1's switch (`AllConsumed`/`FullSheetReturned`/`UsableDropCreated`/`NeedsReview` → `ClosedConsumed`/`ClosedReturned`/`ClosedConsumed`/`Exception`) match the existing `MaterialReservation.cs`/`ProductionConsumption.cs` enum definitions verified against the current codebase (not the spec's prose alone).
