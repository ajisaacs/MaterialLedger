# Purchase order entry & receiving-against-PO — design

## Context

`docs/superpowers/specs/2026-08-09-receive-material-design.md` shipped `ReceiveMaterial`
for logging material that has already physically arrived, and explicitly deferred
pre-arrival order tracking as a named non-goal:

> The spec's `Ordered`/`ExpectedArrivalDateUtc` pre-arrival tracking extension... this
> design covers material that has physically arrived only... deliberately deferred to
> its own follow-up design after `ReceiveMaterial` ships.

This is that follow-up. Vendor order confirmations (O'Neal Steel, Alro, etc. — see the
`Order Confirmations` mail folder) arrive as a header (vendor, PO#, order date) plus a
list of line items (material spec, dimensions, quantity, unit price, vendor part
number, quoted delivery date). Today there is no way to record that an order was
*placed* — only that material was *received* — and no way to see what's still
outstanding on a PO. This design adds a `PurchaseOrder`/`PurchaseOrderLine` layer and
wires receiving to optionally consume against a specific line.

## Non-goals

- Automatic parsing/OCR of vendor confirmation PDFs. Layouts differ per vendor
  (O'Neal's tabular sales acknowledgement vs. Alro's format) and extracting them
  reliably is a materially bigger, vendor-format-specific effort. Line items are typed
  in by hand from the confirmation, same as `ReceiveMaterial` already requires manual
  entry of receipt provenance.
- Multi-currency, tax, or freight tracking. `UnitPrice` per line is enough to know what
  was paid; PO-level totals/tax/freight (present on the O'Neal confirmations) are not
  captured — `CostPerUnit` on `SheetLot`/receiving already exists for landed cost, and
  this design doesn't change it.
- Editing a PO's lines after creation beyond cancel. If a vendor changes quantities or
  pricing on a confirmed order, the simplest correct action is cancelling the affected
  line(s) and creating a new PO/line for the correction — avoids building a full
  line-item edit/audit-trail UI for a rare case.
- Vendor/vendor-part-number master data. `VendorName` and `VendorPartNumber` stay plain
  strings on the PO/line, exactly like `SheetLot.VendorName`/`VendorPartNumber` today —
  no new `Vendor` entity.

## Data model

```csharp
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
    public string? VendorDescription { get; set; }   // raw text, e.g. "3/16 X48 X8"
    public string? VendorPartNumber { get; set; }
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityReceived { get; set; }     // maintained transactionally by ReceiveMaterialService
    public decimal? UsableWidthIn { get; set; }
    public decimal? UsableLengthIn { get; set; }
    public decimal? UnitPrice { get; set; }
    public DateOnly? QuotedDeliveryDate { get; set; }
    public PurchaseOrderLineStatus Status { get; set; }
}
```

- `SheetLot` gains a nullable `PurchaseOrderLineId` (+ `PurchaseOrderLine?` nav) —
  links a physical receipt back to the line it fulfilled. Nullable so today's
  free-form receiving (no PO) is unaffected.
- `Attachment.AttachmentEntityType` gains a `PurchaseOrder` member (enum stored as
  `int`, same as every other enum in this codebase — appending at the end is a
  non-breaking change). Lets a vendor confirmation PDF be uploaded and stored against
  the PO using the existing, currently-ungranted `material.Attachments` table.
- PO header status is **derived**, never stored: `Open` if any line is
  `Open`/`PartiallyReceived`; `Received` if all lines are `Received`/`Cancelled` with
  at least one `Received`; `Cancelled` if every line is `Cancelled`.
- Cancelling is the only manual terminal state, and it lives on the line, not the
  header: `Status = Cancelled` blocks further receiving on that line but preserves
  whatever `QuantityReceived` already accumulated. A PO-level "Close remaining" action
  is a convenience that cancels every line still `Open`/`PartiallyReceived` on that PO
  in one transaction — not a second state machine.

## Services / write flow

### `PurchaseOrderService` (new, `MaterialLedger.Data/PurchaseOrders/PurchaseOrderService.cs`)

Same transactional/idempotency shape as `ReceiveMaterialService`/`OpeningBalanceService`
throughout: SHA-256 payload hash, `OperationLogGuard.CheckAsync`/`Record`,
`Serializable` transaction.

```csharp
public record PurchaseOrderLineRequest(
    long MaterialSpecId,           // resolved by the page handler, same pattern as ReceiveMaterialRequest
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
```

`CreatePurchaseOrderAsync(correlationId, request, createdBy)`:
1. Hash, begin transaction, `OperationLogGuard.CheckAsync` (replay on retry).
2. Validate: `VendorName`/`PurchaseOrderNumber` non-blank, at least one line,
   every line's `QuantityOrdered >= 1`.
3. Insert `PurchaseOrder`, then each `PurchaseOrderLine` (`LineNumber` = 1-based
   position, `Status = Open`, `QuantityReceived = 0`).
4. `OperationLogGuard.Record`, `SaveChangesAsync`, commit.

`CancelLineAsync(correlationId, purchaseOrderLineId, cancelledBy)`: loads the line,
rejects if already `Received`, sets `Status = Cancelled`. Same idempotency shape.

`CloseRemainingAsync(correlationId, purchaseOrderId, closedBy)`: loads all lines for
the PO still `Open`/`PartiallyReceived`, sets each to `Cancelled` in one transaction.
Implemented as a loop over the same per-line cancel logic used by `CancelLineAsync`,
not a separate code path.

### `ReceiveMaterialService` extension

`ReceiveMaterialRequest` gains one new field: `long? PurchaseOrderLineId`. When set,
inside the existing transaction, before the current insert logic:

1. Load the line with the same row-locking the existing location check already
   relies on (`Serializable` isolation); reject if `Status == Cancelled` or
   `Status == Received`.
2. Reject if `request.Quantity > line.QuantityOrdered - line.QuantityReceived`
   (over-receipt is blocked, not warned).
3. After the existing `SheetLot` insert, set `sheetLot.PurchaseOrderLineId = line.PurchaseOrderLineId`.
4. After the existing transaction's inserts, increment
   `line.QuantityReceived += request.Quantity` and recompute `line.Status`
   (`PartiallyReceived` if `0 < QuantityReceived < QuantityOrdered`, else `Received`).

No other part of `ReceiveMaterialService` changes — receiving without a PO line
(`PurchaseOrderLineId == null`) behaves exactly as it does today.

## Pages / UI

New `Pages/PurchaseOrders/` area,
`[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]` (same gate as
`Receiving`/`Census`):

- **`Index.cshtml`**: table of POs — vendor, PO#, order date, derived status badge,
  line count / received progress. Status filter (`Open` default, matching the
  `Open`-default pattern other index pages in this app use).
- **`Create.cshtml`**: header fields (vendor name, PO number, order date, notes) +
  repeatable line rows — each row is either an existing-spec picker or new-spec fields
  (family/grade/finish/thickness, same as `NewReceipt`/`NewOpeningBalance`), plus
  vendor description, vendor part#, quantity ordered, unit price, quoted delivery date,
  usable width/length. Rows added client-side via a small vanilla-JS row-clone (no
  framework — matches the plain-forms style everywhere else in this app). One raw
  `action="?handler=Post"` form + a single explicit `@Html.AntiForgeryToken()`, per the
  standing codebase rule against mixing tag helpers with an explicit token.
- **`Details.cshtml`**: header info, attachment upload/download (new
  `AttachmentStorageService` writes to a configured directory on disk and inserts an
  `Attachments` row — first real usage of that table), line-items table
  (Ordered/Received/Remaining/Status columns) with a **Receive** button per
  `Open`/`PartiallyReceived` line and a **Cancel** button per non-`Received` line, plus
  a header-level **Close remaining** button.

**Receiving flow gets one entry point added, not rebuilt.** The **Receive** button on a
PO line links to `Receiving/SelectLocation?poLineId={id}`; `SelectLocation` passes
`poLineId` through to `Receiving/NewReceipt`, which — when present — loads the line and
pre-fills `ExistingMaterialSpecId`, `VendorName`, `PurchaseOrderNumber`,
`VendorPartNumber`, `CostPerUnit`, `UsableWidthIn`/`UsableLengthIn` (still editable) and
caps `Quantity` at `line.QuantityOrdered - line.QuantityReceived`. Reaching `NewReceipt`
the existing way (from `Census/SelectSlot`, no `poLineId`) is unchanged.

## Permissions / migration

New EF migration: `PurchaseOrders`, `PurchaseOrderLines` tables, `SheetLots.PurchaseOrderLineId`
nullable FK column. The `Attachment.EntityType` enum extension needs no schema change
(stored as `int`).

`docs/schema/material-permissions-v8.sql` (next number after v7, never editing v1–v7):

```sql
GRANT SELECT, INSERT, UPDATE ON material.PurchaseOrders TO materialledger_app;
GRANT SELECT, INSERT, UPDATE ON material.PurchaseOrderLines TO materialledger_app;
GRANT SELECT, INSERT ON material.Attachments TO materialledger_app;
```

`material.Attachments` currently has zero grants (same gap the v5 receive-material
design noted for `SheetLots` before that design closed it) — this is the first feature
to actually write to it. `SheetLots` already has `SELECT`/`INSERT` from v5, unaffected
by the new nullable column. Manual, human-run apply against the real Forge SQL Server,
same as v1–v7.

## Testing

`MaterialLedger.Tests/PurchaseOrderServiceTests.cs`, integration tests against the real
throwaway test DB (`IClassFixture<SqlServerTestDatabaseFixture>`), same shape as
`OpeningBalanceServiceTests.cs`/`ReceiveMaterialServiceTests.cs`:

- Happy path: PO + all lines created atomically, `LineNumber` sequential, `Status = Open`.
- Rejects blank vendor/PO number, zero lines, non-positive line quantity.
- `CancelLineAsync` rejects cancelling an already-`Received` line; succeeds on
  `Open`/`PartiallyReceived`.
- `CloseRemainingAsync` cancels only non-`Received` lines, leaves `Received` lines alone.
- Idempotency: replaying the same correlation ID for create/cancel/close returns the
  original result without a second write.

Extensions to `ReceiveMaterialServiceTests.cs`:
- Receiving against a line with `PurchaseOrderLineId` set links the new `SheetLot`,
  increments `QuantityReceived`, and updates `Status` correctly at partial and full
  receipt.
- Rejects a receive request that would exceed `QuantityOrdered - QuantityReceived`.
- Rejects receiving against a `Cancelled` or already-`Received` line.
- Receiving with `PurchaseOrderLineId == null` is unaffected (existing tests keep passing).

Full existing integration suite re-run to confirm no regressions, per this codebase's
established practice at the end of every task.
