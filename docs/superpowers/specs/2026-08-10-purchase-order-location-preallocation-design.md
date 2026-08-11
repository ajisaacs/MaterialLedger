# Purchase order location preallocation & bulk receive — design

## Context

`docs/superpowers/specs/2026-08-10-purchase-order-entry-and-receiving-design.md` shipped
`PurchaseOrder`/`PurchaseOrderLine` tracking and wired `ReceiveMaterialService` to
optionally consume against a line, but receiving is still strictly one line, one
location, one submission at a time via `Receiving/SelectLocation` → `Receiving/NewReceipt`.

The only PO currently in the system has a line with 28 pieces that needs to be split
across multiple storage shelves when it arrives — no single-location assumption holds.
This design lets a Programmer/Manager plan, ahead of a delivery's arrival, which
shelves a line's quantity will go to (including splitting one line across several
shelves), and then receive everything for a PO in one pass from
`PurchaseOrders/Details` instead of walking each line through the existing
`SelectLocation`/`NewReceipt` flow individually.

## Non-goals

- Changes to `ReceiveMaterialService`. A planned allocation is just a location +
  quantity that pre-fills an ordinary `ReceiveMaterialAsync` call; a split line is N
  calls instead of one, each already legal today. No new receiving logic, no new
  concept of a "multi-location receipt."
- Enforcing that a line's allocations sum to its full ordered quantity. Partial planning
  is allowed — e.g. plan 20 of a 28-piece line across two shelves now, decide the
  remaining 8 later. `ReceiveAll` always surfaces whatever is left unallocated so
  nothing is silently skipped.
- A generic "plan a receipt to N locations" feature detached from purchase orders.
  This is scoped to PO lines, matching how the rest of receiving already requires a PO
  number (`ReceiveMaterialService.Validate` already rejects a blank PO number).
- Changing what a single receipt row represents. One `PurchaseOrderLineAllocation` →
  one `ReceiveMaterialAsync` call → one `SheetLot`/`InventoryItem`, exactly like an
  unplanned receipt today. No change to how inventory is modeled.

## Data model

New entity, added to `MaterialLedger.Data/Models/PurchaseOrder.cs` (child of
`PurchaseOrderLine`, same one-file-per-header-entity convention already used there):

```csharp
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

`PurchaseOrderLine` gains `public List<PurchaseOrderLineAllocation> LocationAllocations { get; set; } = [];`.

EF configuration (new block in `MaterialLedgerDbContext.cs`, alongside the existing
`PurchaseOrderLine` block):

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

`Restrict` on both FKs matches the existing convention (`PurchaseOrderLine.PurchaseOrder`,
`SheetLot.PurchaseOrderLine`, `SheetLot.InventoryLocation` are all `Restrict`) — nothing
in this codebase deletes PO lines or locations, so this only matters as documentation of
intent.

No column added to `PurchaseOrderLine` itself — a line with no allocations behaves
exactly as it does today.

## Services / write flow

### `PurchaseOrderService` additions

Same transactional/idempotency shape as every existing method on this service
(SHA-256 payload hash → `Serializable` transaction → `OperationLogGuard.CheckAsync`/replay
→ business write → `OperationLogGuard.Record` → `SaveChangesAsync` → commit).

```csharp
public record AddLocationAllocationRequest(long PurchaseOrderLineId, long InventoryLocationId, decimal Quantity);
```

**`AddLocationAllocationAsync(correlationId, request, createdBy) : Task<long>`**
1. Load the line; reject if missing, or if `Status` is `Received`/`Cancelled`
   ("can't plan a delivery location for a line that's done").
2. Load the location; reject if not a leaf type (`Shelf`, `Bay`, `Staging`,
   `Quarantine`, `Scrap`) — same rule `ReceiveMaterialService` already enforces, checked
   independently here since this table has no FK-level constraint that could express it.
3. Reject if `request.Quantity < 1` or not a whole number.
4. Compute `alreadyAllocated = line.LocationAllocations.Sum(x => x.QuantityPlanned)`
   (fresh read inside the transaction). Reject if
   `alreadyAllocated + request.Quantity > line.QuantityOrdered - line.QuantityReceived`.
5. Insert the `PurchaseOrderLineAllocation` row.
6. Record, save, commit. Return the new allocation's id.

**`RemoveLocationAllocationAsync(correlationId, allocationId, removedBy) : Task`**
1. Load the allocation; if missing, treat as already-removed (idempotent no-op — matches
   how `CancelLineAsync` etc. don't distinguish "already done" from "just did it" on
   replay).
2. Delete it, record, save, commit.

No update/edit-in-place operation — changing a planned quantity or location is
remove-then-add, consistent with this design's non-goal of line editing and the parent
design's precedent (cancel-and-recreate over in-place edit).

### `PurchaseOrders/ReceiveAll` orchestration (page handler, not a service method)

Bulk receiving is **not** a new service method — it's a loop in the page handler over
the existing `ReceiveMaterialService.ReceiveMaterialAsync`, one call per row the user
has checked. Each call is already its own atomic, idempotently-retryable transaction
(this is exactly what happens today when a line is received one at a time), so:

- A failure partway through a batch doesn't roll back the rows that already succeeded.
- Resubmitting the page after a partial failure is safe — successful rows carry a
  correlation ID already recorded in the operation log, so re-POSTing the whole form
  replays them as no-ops and only the failed rows actually write.

After a row succeeds, if it came from a `PurchaseOrderLineAllocation`, the handler calls
`RemoveLocationAllocationAsync` for that allocation. This call is not fire-and-forget:
because `AddLocationAllocationAsync` bounds new allocations by
`QuantityOrdered - QuantityReceived - alreadyAllocated`, a stale (already-received)
allocation row left behind would wrongly count against future planning. If the removal
call itself fails after the receipt already committed, the row is reported as a warning
on the results screen ("received, but its plan entry couldn't be cleared — remove it
manually") rather than silently ignored.

## Pages / UI

### `PurchaseOrders/Details.cshtml` additions

Each `Open`/`PartiallyReceived` line's row expands to show:
- Its current allocations as a small list: location code, planned quantity, a "Remove"
  button per row (`OnPostRemoveAllocationAsync(long allocationId)`).
- An inline add form: location dropdown (leaf locations, same source query as
  `Receiving/SelectLocation`'s `ActiveMaterialSlotQuery`) + quantity input, with the
  still-unallocated remainder shown as a hint (`QuantityOrdered - QuantityReceived -
  sum(LocationAllocations)`). Posts to `OnPostAddAllocationAsync(long lineId, long
  inventoryLocationId, decimal quantity)`. Both handlers follow the existing
  `OnPostCancelLineAsync` pattern on this page: try the service call, catch
  `InvalidOperationException`/`OperationConflictException`, reload, show `Error`.

A **"Receive all"** link appears whenever the order has at least one
`Open`/`PartiallyReceived` line, pointing to `PurchaseOrders/ReceiveAll?id={poId}`.

### New page `PurchaseOrders/ReceiveAll`

`OnGetAsync(long id)` builds one row per candidate receipt:
- One row per existing `PurchaseOrderLineAllocation` on any `Open`/`PartiallyReceived`
  line of this PO — location and quantity prefilled from the allocation, checkbox
  checked by default.
- One additional row per line that still has unallocated remaining quantity
  (`QuantityOrdered - QuantityReceived - sum(LocationAllocations) > 0`) — location left
  blank (required if checked), quantity prefilled with the unallocated remainder.

Each row also carries, editable: heat/lot number, cost per unit (defaulted from the
line's `UnitPrice`), a quarantine checkbox, and a notes field — the same
receipt-provenance fields `NewReceipt` collects today. Item kind, shape classification,
and usable width/length are **not** editable per row: they default to
`FullSheetBundle`/`Rectangular`/the line's `UsableWidthIn`/`UsableLengthIn`. A line
needing something different (e.g. it turns out to be individual full sheets, not a
bundle) is unchecked here and received the existing way via the per-line **Receive**
link on `Details`.

`OnPostAsync()` loops over checked rows in line order:
1. Validate: location present, quantity ≥ 1 and a whole number.
2. Build a `ReceiveMaterialRequest` (vendor name / PO number from the header, vendor
   part number / cost from the line unless overridden, `PurchaseOrderLineId` set) and
   call `ReceiveMaterialAsync` with a correlation ID bound per-row at `OnGetAsync` time
   (a hidden field, stable across a resubmit so retries replay instead of duplicating).
3. On success: record the new item code for the results banner; if the row came from an
   allocation, call `RemoveLocationAllocationAsync`.
4. On failure (`InvalidOperationException`/`ArgumentException`/`OperationConflictException`):
   record the row's error, leave it checked and editable.

Re-renders the same page: succeeded rows show their new item code and become read-only;
failed or unchecked rows stay editable for a follow-up submit. No separate confirmation
page, since resubmitting is always safe.

## Permissions / migration

New EF migration: `PurchaseOrderLineAllocations` table (16th `material` table, up from
15 today — `SchemaShapeTests.AllFifteenMaterialTables_Exist` gets renamed/bumped to 16,
same rename-and-bump pattern the prior design used going from 13→15).

`docs/schema/material-permissions-v9.sql` (next number after v8, v1–v8 untouched):

```sql
GRANT SELECT, INSERT, DELETE ON material.PurchaseOrderLineAllocations TO materialledger_app;
```

`DELETE` is new here — no prior grant file has needed it, since every other write path
in this app is insert/update-only (cancel-by-status-flip, not delete). Allocations are
the first genuinely deletable row in the schema (`RemoveLocationAllocationAsync`), so
this grant is a deliberate first. Manual, human-run apply against the real Forge SQL
Server, same as v1–v8.

## Testing

`MaterialLedger.Tests/PurchaseOrderServiceTests.cs` additions, same
`IClassFixture<SqlServerTestDatabaseFixture>` integration-test shape as the rest of the
file:

- `AddLocationAllocationAsync` happy path: allocation row created, visible via
  `line.LocationAllocations`.
- Rejects a non-leaf location (e.g. a `Rack` or `Area`).
- Rejects a fractional or zero/negative quantity.
- Rejects when the new allocation would push the line's total allocated quantity over
  `QuantityOrdered - QuantityReceived` (including the case where a prior allocation
  already exists and the new request only tips it over in combination).
- Rejects allocating against a `Received` or `Cancelled` line.
- `RemoveLocationAllocationAsync` deletes the row; is a no-op (not a throw) on an
  already-missing id.
- Idempotency: replaying the same correlation ID for add/remove doesn't duplicate or
  double-delete.

`MaterialLedger.Tests/ReceiveMaterialServiceTests.cs`: unchanged — this design adds no
new behavior to `ReceiveMaterialService`, so no new tests belong there. The `ReceiveAll`
page's per-row loop is exercised at the page level instead (see below), since the
interesting behavior here is the orchestration (partial-batch failure, allocation
cleanup), not anything inside `ReceiveMaterialAsync` itself.

Manual/Playwright pass on `Details` (add/remove allocation, remaining-quantity hint
updates and caps correctly) and `ReceiveAll` (golden path receives every row and clears
allocations; a mid-batch failure — e.g. two rows targeting a location that fails
validation — leaves succeeded rows read-only and the failed row editable; resubmitting
after fixing the failed row doesn't duplicate the already-succeeded ones).

Full existing integration suite re-run to confirm no regressions, per this codebase's
established practice at the end of every task.
