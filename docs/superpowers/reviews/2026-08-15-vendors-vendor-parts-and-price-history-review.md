# Review — Vendors, Vendor Parts, and Pricing History Plan

Reviewed plan: `docs/superpowers/plans/2026-08-15-vendors-vendor-parts-and-price-history.md`

## Findings

### Critical — planned migration/backfill order destroys the PO source data before it can be backfilled

The plan says Task 2 should make a migration which drops `PurchaseOrder.VendorName` and `PurchaseOrderLine.VendorPartNumber` (plan lines 134–137), then says to run the real-db backfill after that migration (line 141). The proposed backfill itself reads both columns (lines 87–104 and 111–124). It therefore cannot seed or link existing PO data after the migration described.

Use a staged rollout:
1. Migration A adds nullable `VendorId`/`VendorPartId` while retaining legacy PO strings.
2. Apply A, run and verify the backfill.
3. Deploy code that reads FKs (with a planned transitional fallback if required).
4. Migration B makes required fields non-null only after an orphan gate, and drops the mutable legacy columns.

The real-db checklist currently lists only two migrations (line 232); it must include this final cleanup migration and an explicit rollback/hold point.

### Critical — the plan violates the stated SheetLot immutability invariant

The plan says SheetLots are append-only and have no UPDATE/DELETE anywhere in the schema (line 13), and existing permission v5 says a SheetLot “once recorded, is never edited or removed.” Yet the Task 2 backfill executes `UPDATE material.SheetLots` to set both new FKs (lines 126–131).

Resolve the policy before implementation. Either explicitly authorize a one-time administrator-only data migration to enrich immutable historical rows (and document/audit it), or preserve strict immutability by using a separate historical-link/mapping mechanism. Do not state “no UPDATE anywhere” while planning an UPDATE of every historical provenance row.

### High — VendorPart canonical uniqueness is not guaranteed, and the backfill can violate its own unique index

The plan normalizes vendor names but indexes raw `(VendorId, PartNumber)` (lines 16 and 64). Its seed query uses `SELECT DISTINCT` before `TRIM(src.PartNumber)` (lines 96–108), so source values such as `"ABC"` and `" ABC "` produce two proposed inserts for the same trimmed raw key and fail. Case-equivalence is also left to database collation rather than modeled.

Add a persisted computed normalized part key (e.g. `UPPER(RTRIM(LTRIM([PartNumber])))`) and a unique index on `(VendorId, NormalizedPartNumber)`. Seed from a normalized projection/grouping, and use the same normalized key for joins and find-or-create. Add tests for whitespace/case duplicates and duplicate source data.

### High — several production paths and DI registrations are outside the task file list

The plan drops mutable PO string properties but only names `Create.cshtml(.cs)` and `NewReceipt.cshtml(.cs)` in Task 6 (lines 221–223). Existing code also references the removed properties in:
- `Pages/PurchaseOrders/ReceiveAll.cshtml.cs` (constructs receipts using `Order.VendorName` and `line.VendorPartNumber`),
- PO Index/Details/ReceiveAll Razor views,
- `Api/Contracts/CommandContracts.cs` and `Api/CommandEndpoints.cs`,
- unit/integration tests and `SchemaShapeTests` seed objects.

These are compile failures or stale public API behavior if omitted. Also, `Program.cs` is absent from the plan but must register `VendorService` and `VendorQueryService`; otherwise the proposed Razor-page constructor injection fails at runtime. Enumerate and update all of these paths, and decide/API-document whether direct API receipt requests submit IDs, snapshot strings, or both.

### High — the schema-shape test baseline is incompatible with adding two tables

Current `SchemaShapeTests.AllSixteenMaterialTables_Exist` asserts exactly 16 tables. Task 1 only says to add cases asserting two new tables/indexes (plan line 73); that existing exact-count test will fail after migration. Update the existing count/name assertion as part of Task 1, not merely add test cases.

### Medium — FK topology is contradictory

The design says the PO line’s vendor is reachable through its PO (line 137), but the actual SQL separately updates `PurchaseOrderLines.VendorId` (lines 115–117) and the narrative at line 137 does not include that property. Pick one model. A header-level `PurchaseOrder.VendorId` plus `PurchaseOrderLine.VendorPartId` is sufficient if `VendorPart.VendorId` is validated against the header vendor. If a line-level VendorId is needed, define it in the model, constraints, UI behavior, and query semantics.

### Medium — quoted/actual eligibility and direct-receipt behavior need explicit contract tests

`PartActualPricePoint` has non-null `CostPerUnit` but `SheetLot.CostPerUnit` is nullable. The plan does not say whether costless receipts are excluded, represented differently, or rejected. Nor does it define how direct receipt/API paths create or resolve a Vendor/VendorPart and snapshot values. Define these rules, validate them in the write path, and add tests for costless receipts, direct receipts, inactive vendors/parts, and PO-vendor/part mismatch.

## Review verdict

Original verdict: do not execute as written. The companion implementation plan was revised on 2026-08-15 to address every finding: staged A/B/C migrations, an explicit audited administrator-only SheetLot association exception, normalized vendor-part keys, complete caller/DI inventory, schema-test correction, one FK topology, and explicit price/direct-receipt contracts. Re-review the revised plan before implementation if repository behavior changes.
