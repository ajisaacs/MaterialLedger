# Receive Material — design

## Context

Phase 0's `SheetLot.SourceType` enum, `MaterialTransactionType.Receipt`, and
`MaterialTransaction.SheetLotId` were all added to the schema in anticipation of a
`ReceiveMaterial` write procedure, but no phase (0–3) has built it. The spec
(`REMCO-Laser-Material-Ledger-Design-Spec.md`) lists it in the controlled write surface
(section 3, "Controlled write surface") with a one-line description:

> **Receive:** add known material to a confirmed location with receipt provenance when
> available.

Permitted roles: Programmer, Manager (same table row as `RecordOpeningBalance`).

Today the only way to add stock to inventory through the UI is
`Census/NewOpeningBalance`, which has no concept of vendor/PO/heat-lot/cost provenance
and is meant for the one-time initial count, not ongoing receiving. This design adds a
dedicated `ReceiveMaterial` procedure and UI, modeled directly on
`OpeningBalanceService`/`Census/NewOpeningBalance` (same transactional shape,
idempotency pattern, and leaf-location enforcement), but scoped specifically to
receiving purchased sheet stock.

## Non-goals

- The spec's `Ordered`/`ExpectedArrivalDateUtc` pre-arrival tracking extension
  (`SheetLot` future-extension note, section 3) — this design covers material that has
  physically arrived only. A due-date calendar of incoming material needs this
  extension directly; it's deliberately deferred to its own follow-up design after
  `ReceiveMaterial` ships, rather than folded in here, since the schema note already
  confirms it can be added later without reworking anything in this design.
- No changes to `Inventory/Search` beyond what it already displays for items with a
  `SheetLotId`.
- No merging of a new receipt into an existing `InventoryItem` at the same location —
  every receipt is its own `SheetLot` + `InventoryItem`, so per-receipt vendor/PO/heat-lot
  provenance is never blended across sheets that arrived under different terms.

## Data flow

New service: `MaterialLedger.Data/Receiving/ReceiveMaterialService.cs`, following the
exact `OpeningBalanceService` shape:

1. Hash the request payload (SHA-256), begin a `Serializable` transaction.
2. `OperationLogGuard.CheckAsync` — replay a prior result for the same correlation ID or
   proceed.
3. Resolve and validate the location (must be a leaf type: `Shelf`, `Bay`, `Staging`,
   `Quarantine`, or `Scrap` — never `Area`/`Rack`, same check as `OpeningBalanceService`).
4. Insert a new `SheetLot` (`SourceType = Receipt`, `ReceivedAtUtc = now`).
5. Pull the next `material.ItemCodeSeq` value and insert a new `InventoryItem`
   (`ItemKind` restricted to `FullSheetBundle`/`FullSheet`, `SheetLotId` = the new lot's
   id, `Status = Quarantined` or `Available` depending on the quarantine checkbox,
   `PublicItemCode = "SHEET-{seq}"`).
6. Insert a `MaterialTransaction` (`TransactionType = Receipt`, `SheetLotId` set,
   `QuantityDelta = quantity`, `SourceSystem = OperatorUI`).
7. `OperationLogGuard.Record` (sync), one final `SaveChangesAsync`, `CommitAsync`.

All three inserts happen in the same transaction as the existing patterns in this
codebase — no partial state is ever visible.

## Request / result shape

```csharp
public record ReceiveMaterialRequest(
    long InventoryLocationId,
    long MaterialSpecId,            // already resolved by the page handler
    InventoryItemKind ItemKind,     // FullSheetBundle | FullSheet only
    decimal Quantity,               // whole sheets, >= 1
    decimal? UsableWidthIn,
    decimal? UsableLengthIn,
    ShapeClassification ShapeClassification,
    bool IsQuarantined,
    string? VendorName,
    string PurchaseOrderNumber,     // required
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
```

`MaterialSpecId` is resolved before the service is called — the page handler holds its
own `[BindProperty] long? ExistingMaterialSpecId` / `MaterialFamily` / `Grade` /
`Finish` / `ThicknessIn` fields and calls `MaterialSpecService.GetOrCreateAsync` when no
existing spec was chosen, exactly like `NewOpeningBalanceModel.OnPostAsync` does today.
This keeps the service's own surface consistent with `OpeningBalanceRequest`, which also
takes a plain `MaterialSpecId`.

### Validation (`Validate(request)`, mirrors `OpeningBalanceService.Validate`)

- `ItemKind` must be `FullSheetBundle` or `FullSheet` — reject `Drop` (defense in depth;
  the page's kind selector won't offer it either).
- `Quantity` must be a whole number ≥ 1 — no fractional-sheet receiving.
- `PurchaseOrderNumber` must be non-null/non-whitespace.
- Location leaf-type check happens inside the transaction (needs a DB round trip),
  same placement as `OpeningBalanceService`.

## Pages / UI

Reuse the existing `Census/SelectSlot.cshtml` location picker rather than duplicating
its query in a new page: add a second action link next to "Record opening balance" —
"Receive material" — pointing at `/Receiving/NewReceipt?locationId={id}`. One shared
slot-list screen serves both actions.

New page: `Pages/Receiving/NewReceipt.cshtml` + `.cshtml.cs`
(`[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]`), modeled on
`Census/NewOpeningBalanceModel`:

- `OnGetAsync(long locationId)`: resolve `Location`, load `AvailableSpecs`
  (`MaterialSpecService.SearchAsync(null)`), same as Opening Balance. No
  "copy from existing item" convenience here (that only made sense for opening balance's
  "reuse the last count's geometry" use case; receiving is a superset of provenance
  fields with fresh values every time).
- Form fields: existing-spec picker OR new-spec fields (family/grade/finish/thickness),
  item kind (`FullSheetBundle`/`FullSheet` only — the `<select>` omits `Drop`), quantity,
  usable width/length, shape classification, quarantine checkbox, vendor name, PO number
  (marked required in the UI), receipt number, vendor part number, heat/lot number, cost
  per unit, notes.
- `OnPostAsync()`: same validation-before-service-call pattern as
  `NewOpeningBalanceModel` (spec resolution error, stale-spec-id error,
  `ArgumentException`/`InvalidOperationException`/`OperationConflictException` handling),
  calls `ReceiveMaterialService.ReceiveMaterialAsync`, redirects to
  `Receiving/Success?code={PublicItemCode}`.
- Antiforgery: raw `action="?handler=Post"` + a single explicit
  `@Html.AntiForgeryToken()` — per the standing codebase rule (`docs/PROGRESS.md`
  session log, hit 3 times already) never combine `asp-page`/`asp-route-*` tag helpers
  with an explicit token on the same form.

New page: `Pages/Receiving/Success.cshtml` + `.cshtml.cs` — same shape as
`Census/Success.cshtml` (displays the new `PublicItemCode`, link back to
`Census/SelectSlot` to receive another).

## Permissions

New grants file `docs/schema/material-permissions-v5.sql`, following the one-file-per-
phase convention (never edit v1–v4):

```sql
GRANT SELECT, INSERT ON material.SheetLots TO materialledger_app;
```

`material.SheetLots` currently has **zero** grants to `materialledger_app` — this is the
only new grant needed. `InventoryItems`, `MaterialTransactions`, `ItemCodeSeq`, and
`MaterialSpecs` are already granted from v1/v2 (opening-balance work) and cover this
procedure's other writes. Applying this file to the real Forge SQL Server is a manual,
human-run step, same as v1–v4 — no automation runs it.

## Testing

`MaterialLedger.Tests/ReceiveMaterialServiceTests.cs`, integration tests against the
real throwaway test DB (`IClassFixture<SqlServerTestDatabaseFixture>`), same shape as
`OpeningBalanceServiceTests.cs`:

- Happy path: new `SheetLot` + `InventoryItem` + `MaterialTransaction` created
  atomically, `PublicItemCode` follows the `SHEET-` sequence, `SheetLotId` correctly
  linked on both the item and the transaction.
- Rejects `ItemKind.Drop`.
- Rejects missing/whitespace `PurchaseOrderNumber`.
- Rejects non-leaf location (e.g. a `Rack`).
- Rejects fractional quantity.
- Quarantine checkbox sets `InventoryItem.Status = Quarantined` instead of `Available`.
- Idempotency: replaying the same correlation ID returns the original result without a
  second insert.

Full existing integration suite re-run to confirm no regressions, per this codebase's
established practice at the end of every task.
