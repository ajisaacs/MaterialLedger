# Vendors, Vendor Parts, and Pricing History — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` task-by-task. Check off each step only after its stated verification passes.

**Goal:** Normalize vendor identity into `Vendors` and `VendorParts`, link POs and SheetLots without losing provenance, and show per-vendor-part price history derived from receipts and PO quotes.

**Architecture:** `Vendors` and `VendorParts` are the only mutable reference tables. Prices remain derived: a priced receipt is an actual point from `SheetLots`; a PO line with `UnitPrice` is a quote. The rollout is deliberately staged: add nullable FKs while retaining legacy strings, administratively backfill and validate, deploy FK-aware code, then remove obsolete mutable PO strings only in a final migration. SheetLot string values remain immutable receipt snapshots.

**Tech Stack:** ASP.NET Core Razor Pages, EF Core / SQL Server, xUnit integration tests with `SqlServerTestDatabaseFixture`.

---

## Global constraints and decisions

- **No price table in this phase.** `VendorPart` contains no price column and `VendorPartPricePoints` is out of scope. Price history is queried from source records only.
- **Actual-price eligibility:** an actual point is a `SheetLot` with `SourceType == Receipt`, a non-null `VendorPartId`, non-null `ReceivedAtUtc`, and non-null `CostPerUnit`. A costless receipt is valid provenance but is excluded from actual-price lists, last price, and average price. It must not be converted to zero.
- **Quote eligibility:** a quote point is a PO line with non-null `VendorPartId` and non-null `UnitPrice`; all statuses are returned with their status so cancelled quotes are visible but distinguishable. `OrderDate` is nullable and is displayed as unavailable when absent.
- **SheetLot provenance:** the application role remains append-only: it retains `SELECT, INSERT` only on `material.SheetLots`; application code never updates or deletes a SheetLot. The historical FK enrichment in Task 2 is the single, administrator-run exception: it only fills previously-null association columns, does not alter snapshot or money values, is dry-run/transaction/audit guarded, and is documented as such in the v5 grant commentary. Future receipt code writes snapshots and FKs on insert.
- **Snapshot policy:** `SheetLot.VendorName` and `VendorPartNumber` remain immutable receipt-time strings. They are populated by the service from the selected reference entities, never trusted from a browser/API request.
- **PO policy:** after the final cleanup migration, `PurchaseOrder.VendorName` and `PurchaseOrderLine.VendorPartNumber` are removed. `PurchaseOrder` owns required `VendorId`; `PurchaseOrderLine` owns nullable `VendorPartId`. A line does **not** get a duplicate `VendorId`; its vendor is reached through the header. `PurchaseOrderLine.VendorPartId`, when present, must belong to the header vendor and the service validates that invariant.
- **Canonical lookup keys:** database-computed, persisted keys only. `Vendors.NormalizedName = UPPER(RTRIM(LTRIM(Name)))`; `VendorParts.NormalizedPartNumber = UPPER(RTRIM(LTRIM(PartNumber)))`. Unique indexes are `Vendors(NormalizedName)` and `VendorParts(VendorId, NormalizedPartNumber)`. App code trims display values for storage but never assigns computed values.
- **Activation:** new PO/direct-receipt entry may select only active vendors and vendor parts. Receiving an already-created PO is allowed when its referenced vendor/part was later deactivated, so historical orders remain receivable. No delete UI exists.
- **Concurrency:** every multi-query method sharing a scoped `DbContext` awaits queries sequentially; do not use `Task.WhenAll`. Writes use unique indexes as the final uniqueness authority. The PO’s existing serializable/idempotent transaction must check `OperationLogGuard` before a find-or-create call so an idempotent replay does not create an unused vendor.
- **Tests:** all new data tests are xUnit `[Trait("Category", "Integration")]` plus `IClassFixture<SqlServerTestDatabaseFixture>`; no mocks/in-memory provider. They require `MATERIALLEDGER_TEST_ADMIN_CONNECTION` and must fail loudly when absent.
- **Deployment:** use `scripts/rebuild-and-publish-local.sh`; final verification requires local health plus `http://hermes.lan:59406/`.
- **Authorization:** vendor management, PO creation, and receipt workflows use `MaterialLedgerPolicies.RequireProgrammerOrManager`. Operator users must have no create/edit vendor action.

---

## Task 1: Reference entities and migration A

**Objective:** Add normalized reference tables without changing existing PO/SheetLot storage or behavior.

**Files:**
- Create: `MaterialLedger.Data/Models/Vendor.cs`
- Create: `MaterialLedger.Data/Models/VendorPart.cs`
- Modify: `MaterialLedger.Data/MaterialLedgerDbContext.cs`
- Create (EF): migration `AddVendorsAndVendorParts`
- Modify: `MaterialLedger.Tests/SchemaShapeTests.cs`
- Create: `docs/schema/material-permissions-v10.sql`

**Model contract:**
```csharp
public class Vendor
{
    public long VendorId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty; // computed/persisted
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class VendorPart
{
    public long VendorPartId { get; set; }
    public long VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    public string PartNumber { get; set; } = string.Empty;
    public string NormalizedPartNumber { get; private set; } = string.Empty; // computed/persisted
    public string? Description { get; set; }
    public decimal? MinimumOrderQty { get; set; }
    public string? Unit { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
```

**Steps:**
- [ ] Add `DbSet<Vendor>`/`DbSet<VendorPart>`. Configure max lengths (Name 200, PartNumber 100), computed persisted normalized keys, unique normalized indexes, and `DeleteBehavior.Restrict`. Add database check constraints rejecting blank trimmed names/part numbers; service validation is not the only integrity boundary.
- [ ] Generate `AddVendorsAndVendorParts`; inspect `Up`/`Down` to confirm it creates exactly these two tables and does not touch PO/SheetLot columns.
- [ ] Update the existing exact table-count/name assertion in `SchemaShapeTests` from sixteen to eighteen, then add tests that inspect both computed columns, both unique indexes, and rejection of whitespace/case-equivalent vendor parts.
- [ ] Run `dotnet test --filter SchemaShapeTests` against the temp SQL database; confirm all schema assertions pass.
- [ ] Create `material-permissions-v10.sql`, matching the prior grant-file header and manual-admin instructions. It grants `SELECT, INSERT, UPDATE` on `material.Vendors` and `material.VendorParts` to `materialledger_app`, is idempotent, and states it must be run after migration A. Do not apply it to the real DB yet.

## Task 2: Migration B, audited historical association backfill, and validation gate

**Objective:** Add nullable associations without losing legacy source strings, then associate valid historical rows under an explicit, audited admin exception.

**Files:**
- Modify: `MaterialLedger.Data/Models/PurchaseOrder.cs`
- Modify: `MaterialLedger.Data/Models/SheetLot.cs`
- Modify: `MaterialLedger.Data/MaterialLedgerDbContext.cs`
- Create (EF): migration `AddVendorReferences`
- Create: `docs/schema/material-data-vendors-backfill-v1.sql`
- Modify: `docs/schema/material-permissions-v5.sql`
- Modify: `MaterialLedger.Tests/SchemaShapeTests.cs`

**Migration B contract:**
- `PurchaseOrder`: add nullable `long? VendorId` and navigation. Retain required `VendorName` until Task 7.
- `PurchaseOrderLine`: add nullable `long? VendorPartId` and navigation. Retain `VendorPartNumber` until Task 7. Do not add `VendorId`.
- `SheetLot`: add nullable `VendorId`/`VendorPartId` and navigation; retain both snapshot strings permanently.
- Add restrictive FKs and indexes for each FK. Do not add a non-null constraint or drop any legacy column in this migration.

**Backfill script requirements:**
- [ ] Start with an explicit `USE MaterialLedger`, a preflight-only mode, `SET XACT_ABORT ON`, transaction boundaries, and a rollback-on-any-validation-failure path. Record execution time, executing login, source/updated/orphan counts, and script version in an admin-visible audit output (a result set is sufficient; do not insert a fake app operation log).
- [ ] Seed vendors from a normalized, deduplicated union of nonblank `PurchaseOrders.VendorName` **and** `SheetLots.VendorName`. Seed vendor parts from normalized `(vendor, part)` pairs from both PO lines and SheetLots. The deduplication projection must apply `LTRIM/RTRIM/UPPER` before `DISTINCT`/grouping and joins must use normalized computed keys.
- [ ] Set PO `VendorId`, PO-line `VendorPartId` (joining the line’s header vendor), and SheetLot association FKs. The SheetLot update is limited to rows whose target FK is null and only fills keys derived from their immutable snapshot text. It must never update `VendorName`, `VendorPartNumber`, `CostPerUnit`, timestamps, or source type.
- [ ] Before commit, fail the script if any nonblank legacy PO vendor has no vendor FK, any nonblank PO vendor-part has no matching vendor-part FK, or any SheetLot has a nonblank vendor/part snapshot which should resolve but does not. Rows with a blank historical string are reported separately as intentionally unlinked, not silently rewritten.
- [ ] Update `material-permissions-v5.sql` commentary to say the **application login** has no SheetLot UPDATE/DELETE grant, and cross-reference this one administrator-only enrichment script. Do not change the `GRANT SELECT, INSERT` statement.

**Steps:**
- [ ] Generate and review migration B; run the expanded schema tests.
- [ ] Apply migration B to the real database while legacy code remains compatible.
- [ ] Run the script in preflight mode first. Save counts and every unresolved sample in the deployment record; do not proceed if a supposed resolvable row is orphaned.
- [ ] Have an administrator run commit mode once. Re-run the verification queries and retain their output with the deployment evidence. No application login receives elevated permissions.
- [ ] Keep the legacy string paths active until the FK-aware deployment in Tasks 3–6 is verified.

## Task 3: Vendor write service and registration

**Objective:** Provide validated create/edit and normalized lookup/create operations.

**Files:**
- Create: `MaterialLedger.Data/Vendors/VendorService.cs`
- Create: `MaterialLedger.Tests/VendorServiceTests.cs`
- Modify: `MaterialLedger.Web/Program.cs`

**Public contract:**
```csharp
public record CreateVendorRequest(string Name, string? ContactName, string? ContactEmail, string? ContactPhone, string? Notes);
public record UpdateVendorRequest(string Name, string? ContactName, string? ContactEmail, string? ContactPhone, string? Notes, bool IsActive);
public record CreateVendorPartRequest(long VendorId, string PartNumber, string? Description, decimal? MinimumOrderQty, string? Unit);

public class VendorService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    public Task<Vendor> CreateVendorAsync(Guid correlationId, CreateVendorRequest request, int executedBy);
    public Task<Vendor> UpdateVendorAsync(long vendorId, UpdateVendorRequest request, int executedBy);
    public Task<VendorPart> CreateVendorPartAsync(Guid correlationId, CreateVendorPartRequest request, int executedBy);
    public Task<Vendor> FindOrCreateActiveVendorAsync(string name, int executedBy);
    public Task<VendorPart?> FindOrCreateActivePartAsync(long vendorId, string? partNumber, int executedBy);
}
```

**Steps:**
- [ ] Write failing integration tests for blank/overlength validation, normalized vendor and part duplicates (whitespace and case), part ownership, deactivation rather than delete, and concurrent same-key creation.
- [ ] Implement sequential DB access. Explicit create/update methods use `OperationLogGuard`; lookup/create methods use canonical keys and handle the unique-index race without returning duplicate entities. A caller’s existing database transaction must remain usable after the race path.
- [ ] Register `VendorService` as scoped in `Program.cs`.
- [ ] Run `dotnet test --filter VendorServiceTests` until green.

## Task 4: Make PO and receipt write paths FK-authoritative

**Objective:** Ensure all current write entry points establish correct associations and generate SheetLot snapshots server-side.

**Files:**
- Modify: `MaterialLedger.Data/PurchaseOrders/PurchaseOrderService.cs`
- Modify: `MaterialLedger.Data/Receiving/ReceiveMaterialService.cs`
- Modify: `MaterialLedger.Data/Models/PurchaseOrder.cs`
- Modify: `MaterialLedger.Data/Models/SheetLot.cs`
- Modify: `MaterialLedger.Web/Api/Contracts/CommandContracts.cs`
- Modify: `MaterialLedger.Web/Api/CommandEndpoints.cs`
- Modify: `MaterialLedger.Tests/PurchaseOrderServiceTests.cs`
- Modify: `MaterialLedger.Tests/ReceiveMaterialServiceTests.cs`
- Modify: `MaterialLedger.Tests/ReceiveAllIntegrationTests.cs`

**Request rules:**
- PO create accepts `VendorId` and each line’s optional `VendorPartId`, not free-text strings. It verifies the vendor/parts exist, are active, and each part belongs to the selected vendor. `PurchaseOrderService` checks idempotency first, then creates/links inside its serializable transaction.
- Direct receipt/API request accepts `VendorId?` and `VendorPartId?`; if a part ID is supplied it must belong to the vendor. New direct receipt selection requires active reference rows. The service derives `SheetLot.VendorName`/`VendorPartNumber` from those rows.
- A receipt against a PO line ignores client-supplied vendor/part identity and derives it from the PO and line. It rejects a supplied ID that disagrees with that authoritative order. It is allowed to use inactive references because the PO already exists.
- The command API changes its receive-material contract to IDs. This is a deliberate breaking API change; update API tests and any consumer documentation in the same commit. Do not accept client-provided snapshot strings as an alternative source of truth.

**Steps:**
- [ ] Write failing tests for PO vendor/part mismatch, inactive new-order selection, direct receipt part/vendor mismatch, PO receipt client-tampering, snapshot generation, and costless receipt exclusion eligibility.
- [ ] Implement the service changes and update all constructor dependencies/DI registrations needed by the new services.
- [ ] Run the focused PO, receipt, bulk-receive, and command API integration tests.

## Task 5: Read-only vendor query service

**Objective:** Materialize predictable vendor/part lists and derived price history.

**Files:**
- Create: `MaterialLedger.Data/Vendors/VendorQueryService.cs`
- Create: `MaterialLedger.Tests/VendorQueryServiceTests.cs`
- Modify: `MaterialLedger.Web/Program.cs`

**Contract:**
```csharp
public record PartActualPricePoint(DateTime ReceivedAtUtc, decimal CostPerUnit,
    string? PurchaseOrderNumber, string? ReceiptNumber, long SheetLotId);
public record PartQuotedPricePoint(DateOnly? OrderDate, decimal UnitPrice,
    decimal QuantityOrdered, PurchaseOrderLineStatus Status, long PurchaseOrderId);
public record PartPriceSummary(decimal? LastActual, DateTime? LastActualAt,
    decimal? AvgActual, int ActualPoints, int QuotedPoints);
```

**Steps:**
- [ ] Write failing tests that seed two priced receipts, one costless receipt, an inactive part, a quoted PO line, and a cancelled quoted PO line. Assert filtering, descending actual history ordering, nullable order dates, quote status inclusion, average/last math, and empty-history behavior.
- [ ] Implement `GetVendorsAsync`, `GetVendorPartAsync`, actual/quote queries, and summary with sequential awaits only. `GetVendorPartAsync` throws a documented not-found exception; it never fabricates a result.
- [ ] Register `VendorQueryService` scoped in `Program.cs`; run `dotnet test --filter VendorQueryServiceTests`.

## Task 6: Vendor management UI and complete PO/receipt presentation update

**Objective:** Add controlled vendor management and remove all runtime dependence on mutable PO strings.

**Files:**
- Create: `MaterialLedger.Web/Pages/Vendors/{Index,Details,Create,Edit}.cshtml(.cs)`
- Modify: `MaterialLedger.Web/Pages/Shared/_Layout.cshtml`
- Modify: `MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml(.cs)`
- Modify: `MaterialLedger.Web/Pages/PurchaseOrders/Index.cshtml`
- Modify: `MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml(.cs)`
- Modify: `MaterialLedger.Web/Pages/PurchaseOrders/ReceiveAll.cshtml(.cs)`
- Modify: `MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml(.cs)`
- Modify: affected presentation, API, and service tests discovered by repository-wide search for `VendorName` and `VendorPartNumber`

**Steps:**
- [ ] Implement Vendors list/details/create/edit. Details displays part table and selected-part actual/quoted history. Create/edit are `[Authorize(Policy = RequireProgrammerOrManager)]`; edit exposes deactivation, never delete.
- [ ] Replace PO entry’s free-text vendor/part controls with active vendor/part pickers. A clearly labelled inline “new vendor/new part” path invokes `VendorService` and then submits IDs; it must not post untrusted display strings as the PO source.
- [ ] Update PO index/details/receive-all loading queries to include `Vendor` and `VendorPart` where required, and display names/part numbers through navigation properties.
- [ ] Update manual receipt to use IDs. For a PO line, prefill and lock to the PO’s referenced vendor/part. For a direct receipt, picker values are optional but validated by the service.
- [ ] Run a repository-wide `VendorName|VendorPartNumber` search. Each remaining occurrence must be either an intentional SheetLot snapshot/backfill reference, a transitional migration-only reference, or be removed/updated. Update test seed objects as required.
- [ ] Browser-test on the LAN endpoint with an authenticated Programmer session: create vendor/part, create PO using existing and inline-created vendor, receive against its PO line, and confirm the SheetLot carries correct immutable strings and FKs. Verify Operator cannot see or reach vendor create/edit.

## Task 7: Migration C cleanup and ship

**Objective:** Remove obsolete mutable PO strings only after production evidence proves every applicable row is associated.

**Files:**
- Modify: `MaterialLedger.Data/Models/PurchaseOrder.cs`
- Modify: `MaterialLedger.Data/Models/SheetLot.cs`
- Modify: `MaterialLedger.Data/MaterialLedgerDbContext.cs`
- Create (EF): migration `RemoveLegacyPurchaseOrderVendorStrings`
- Modify: impacted tests and schema assertions

**Steps:**
- [ ] Run final real-DB gate queries. Block migration C if any PO lacks `VendorId`, or any nonblank legacy PO part lacks `VendorPartId`. Record blank/no-part lines separately as allowed nulls.
- [ ] Generate migration C: make `PurchaseOrders.VendorId` required, drop only `PurchaseOrders.VendorName` and `PurchaseOrderLines.VendorPartNumber`, and leave all SheetLot snapshot columns untouched. Review `Down` before apply.
- [ ] Run `dotnet build` and full `dotnet test` with the integration suite; no test may be silently skipped for a missing admin connection.
- [ ] Apply production migrations in exact order: A `AddVendorsAndVendorParts` → B `AddVendorReferences` → preflight backfill → committed audited backfill → v10 grants → deploy Tasks 3–6 → final validation gate → C `RemoveLegacyPurchaseOrderVendorStrings` → restart app.
- [ ] Run `scripts/rebuild-and-publish-local.sh`; verify local health and `http://hermes.lan:59406/`. In an authenticated real session, render a vendor detail page with actual and quote history.
- [ ] Commit logical chunks with evidence: schema A/grants; schema B/backfill; write paths; query/UI; schema C/deployment evidence. Update sanitized DR/config notes only if live configuration changed.

---

## Deferred decision: explicit price-point table

Add `VendorPartPricePoints` only when the product must preserve price information not tied to a receipt or PO quote (for example, list-price changes or informal asking prices). If added later, backfill eligible receipt/quote points first and define one canonical write source. Until then, Task 5’s derived queries are the sole price-history source.

## Final implementation checklist

- [ ] No migration removes a source string before its backfill has committed and passed validation.
- [ ] The only historical SheetLot update is the documented admin-only FK enrichment; application grants remain append-only.
- [ ] Canonical database keys protect vendor and part uniqueness under whitespace/case variants.
- [ ] PO, direct receipt, bulk receive, Razor pages, API contracts, DI registration, presentation pages, and tests all use the agreed FK model.
- [ ] Price calculations exclude null-cost receipts and never treat them as zero.
- [ ] All multi-query `DbContext` code is sequentially awaited.
