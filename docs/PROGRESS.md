# Material Ledger — Cross-Session Progress / Handoff

> Keep this file current as work proceeds. Its purpose is to let a future session (in particular,
> Phase 4) pick up without re-deriving the codebase from scratch. Update the "Current status" and
> "Session log" sections whenever a task finishes; don't let it drift.

**Spec:** `C:\Users\aisaacs\Desktop\Projects\REMCO-Laser-Material-Ledger-Design-Spec.md` — this is the
authoritative design document. Read it before making any design judgment call not already covered
below.

**Repo:** `C:\Users\aisaacs\Desktop\Projects\MaterialLedger` — solution `MaterialLedger.sln`, three
projects:
- `MaterialLedger.Data` — EF Core 9 / SQL Server backend. All writes go through plain C# "service"
  classes (NOT stored procedures), one per write surface area, each following an identical shape:
  primary-constructor DI, SHA-256 request-payload hash, `BeginTransactionAsync(IsolationLevel.Serializable)`,
  `OperationLogGuard.CheckAsync` → replay-or-proceed, business mutation, `OperationLogGuard.Record`
  (sync, not `RecordAsync`), one `SaveChangesAsync()`, `CommitAsync()`. Copy this shape exactly for any
  new write procedure — don't improvise a different pattern.
- `MaterialLedger.Web` — ASP.NET Core **Razor Pages** (not Blazor). Cookie auth, per-device
  long-lived session (`ExpireTimeSpan = 3650 days`), roles `Programmer`/`Operator`/`Manager` via
  `[Authorize(Policy = MaterialLedgerPolicies.*)]` on pages plus in-handler `User.IsInRole(...)` checks
  on POST handlers of shared-GET pages. Never trust a client-supplied `AppUserId` — always resolve via
  `User.FindFirst(ClaimTypes.NameIdentifier)`.
- `MaterialLedger.Tests` — xUnit, **integration tests against a real SQL Server**, not mocks/in-memory.
  `[Trait("Category", "Integration")]` + `IClassFixture<SqlServerTestDatabaseFixture>` (throwaway
  `MaterialLedgerTest` DB, requires `MATERIALLEDGER_TEST_ADMIN_CONNECTION` env var, deliberately not
  skipped if missing). A separate `MaterialTransactionPermissionsTests.cs` connects to the REAL
  `MaterialLedger` DB as `materialledger_app` (`MATERIALLEDGER_TEST_APP_CONNECTION`) to prove DB-level
  DENY grants are actually enforced, not just documented.

Grants for the live ShopOps `materialledger_app` login are added incrementally in
`docs/schema/material-permissions-vN.sql` — one new file per phase, never edit prior ones. Applying
them to the real Forge SQL Server database is a **manual, human-run administrative step** — the app
never applies its own grants, and no automation should attempt to run these against forge-sql.

Plans live in `docs/superpowers/plans/YYYY-MM-DD-material-ledger-phaseN.md`, written via the
`superpowers:writing-plans` skill and (recommended) executed via `superpowers:subagent-driven-development`.

## Current status (updated 2026-08-10)

| Phase | Status | Plan doc |
|---|---|---|
| 0 — schema, login, idempotency | **Done** | (no separate plan doc — built directly) |
| 1 — locations, opening census | **Done** | `docs/superpowers/plans/2026-08-07-material-ledger-phase1.md` |
| 2 — reservations, inventory view | **Done** | `docs/superpowers/plans/2026-08-07-material-ledger-phase2.md` |
| 3 — manual closeout, exceptions | **In progress — controlled physical pilot remains** | `docs/superpowers/plans/2026-08-08-material-ledger-phase3.md` |
| Shop-floor workflow redesign | **Source implementation complete — uncommitted local preview; controlled physical pilot/deployment remain** | `.hermes/plans/2026-08-10_020904-shop-floor-workflow-redesign.md` |
| 4 — reporting, scheduling input | **Not started** | — |

Repo was 1 commit ahead of `origin/master`, not pushed, as of session start — check `git status`/`git log` before assuming remote is current.

### What exists today (Phases 0–2, all done)

- Schema: `docs/schema/material-schema-v1.sql` (EF-migration-generated) — full `material` schema
  including tables Phase 3 needed (`MaterialExceptions`, `ProductionConsumptions`) already existed as
  inert tables from the Phase 0 `InitialCreate` migration. **No new EF migration was needed for Phase 3**
  — same is likely true for Phase 4 unless it needs new columns (e.g. `SheetLot.Ordered` extension
  mentioned in spec section 3).
- Write services: `RecordOpeningBalance` (`MaterialLedger.Data/Census/OpeningBalanceService.cs`),
  `ConfirmLocationSlots` (`MaterialLedger.Data/Locations/LocationSlotService.cs`),
  `CreateReservation`/`ApproveReservation`/`ReleaseReservation`
  (`MaterialLedger.Data/Reservations/ReservationService.cs`),
  `ReceiveMaterial` (`MaterialLedger.Data/Receiving/ReceiveMaterialService.cs`).
- Read services: `MaterialLedger.Data/Inventory/InventoryQueryService.cs` (`SearchAsync`,
  `GetItemsAtLocationsAsync`, `GetRackSummariesAsync`, static `MaterialFreshness.IsStale`),
  `MaterialLedger.Data/Materials/MaterialSpecService.cs`.
- Pages: `Login`/`Logout`, `Census/{SelectSlot,NewOpeningBalance,Success}`,
  `Locations/{Index,Details,ConfirmSlots,Labels}`, `Inventory/Search`,
  `Reservations/{Index,Details,Create}`, `Receiving/{NewReceipt,Success}`.
- Still **not built by any phase so far** (present in spec's "controlled write surface" but not in
  Phase 0–3's explicit task lists — no phase has scheduled them yet): `TransferInventory`,
  `RecordAdjustment`, `SetAppUserPassword`, `ConfirmObservation`. Phase 4 does not appear to need most
  of these either per spec section 7, but check before assuming.

### Phase 3 scope (per spec section 7)

1. Manual closeout screen (`Approved`/`AwaitingCloseout` reservation list, optional `Start job`
   flag via `MarkReservationAwaitingCloseout`, `Close material` action) + exception queue +
   `ResolveMaterialException`.
2. All four closeout outcomes (`AllConsumed`, `FullSheetReturned`, `UsableDropCreated`,
   `NeedsReview`) implemented atomically via `CloseoutConsumption`.
3. Controlled pilot across laser + at least one other equipment type (plasma or shear) covering all
   four outcomes, reconciling expected vs. measured before calling Phase 3 done — this is a **live
   verification step against the real Forge SQL Server**, not something automatable in this repo; it's
   the last task in the Phase 3 plan and requires REMCO's physical participation.

Key design decisions made while planning Phase 3 (not spelled out verbatim in the spec — recorded here
so Phase 4 doesn't have to re-derive them):

- `ReservationStatus.ClosedConsumed` used for both `AllConsumed` and `UsableDropCreated` outcomes
  (material was consumed in both cases); `ClosedReturned` for `FullSheetReturned`; `Exception` for
  `NeedsReview`. No new enum values were needed — the existing `ReservationStatus` enum already had
  exactly the right terminal states.
- `MaterialException.ExceptionType.OutcomeUnknown` is opened directly (and atomically) by
  `CloseoutConsumption`'s `NeedsReview` branch — it is not part of the periodic/on-demand scan.
- A `MaterialExceptionService.ScanAsync()` (run on-demand at the top of the Exceptions page's
  `OnGetAsync`, no background job) opens exceptions for `QuarantinedMaterial` (any
  `InventoryItem.Status == Quarantined`), `StaleObservation` (via existing `MaterialFreshness.IsStale`),
  and `AwaitingCloseoutOverdue` (any `Approved`/`AwaitingCloseout` reservation whose `ApprovedAtUtc` is
  older than a new `CloseoutOverdueThresholdDays` config value — there's no separate
  "marked-awaiting-closeout-at" timestamp on `MaterialReservation`, so `ApprovedAtUtc` is the clock used
  for both states). `GeometryMismatchOrInsufficientBalance` has **no automatic detector built in Phase
  3** — it's a valid enum value with nothing populating it yet; flagged as an explicit non-goal, not an
  oversight.
- `ResolveMaterialException` only marks the exception row `Fixed` with actor/timestamp/notes — it does
  **not** automatically flip the underlying item/reservation state back (e.g., a resolved
  `QuarantinedMaterial` exception does not un-quarantine the item). This is intentional: no
  `ConfirmObservation`/`RecordAdjustment` procedure exists yet to re-validate physical state, so
  auto-reverting would violate spec section 1 rule 1 ("unknown material is not available material").
  A resolved-but-still-broken condition will legitimately reopen on the next scan — this matches the
  spec's literal wording in section 4E ("resurface only if their underlying condition returns").
- `CloseoutOverdueThresholdDays` default chosen as a judgment call (spec doesn't give a number,
  unlike the 90-day freshness default) — check `appsettings.json` for the value actually used and
  reconsider with REMCO if it proves noisy in the pilot.

### Known follow-ups (ReceiveMaterial, recorded for whoever picks up the next phase)

- (a) `SheetLot` provenance (vendor, PO number, receipt number, vendor part number,
  heat/lot number, cost per unit) is captured on receipt by `ReceiveMaterialService` but
  is not surfaced anywhere in the UI yet — `Inventory/Search` and every item-detail view
  show nothing from `SheetLot` today. A future phase should add a read path for it
  (`docs/superpowers/specs/2026-08-09-receive-material-design.md`'s Non-goals section was
  corrected to state this plainly rather than implying `Inventory/Search` already shows
  something for `SheetLotId` items).
- (b) Marking a receipt "Quarantined" causes `MaterialExceptionService.ScanAsync` to open
  a `QuarantinedMaterial` exception that reopens on every scan indefinitely, per the
  existing Phase 3 design decision that resolving an exception never auto-reverts the
  underlying item state. This is expected/known behavior today, not a bug — but it will
  need a `ConfirmObservation`/`RecordAdjustment` procedure eventually to break the cycle
  (same procedure already flagged as missing in the "not built by any phase so far" list
  above).
- (c) There is no duplicate-receipt guard — submitting the same physical delivery twice
  creates two separate `SheetLot`/`InventoryItem` rows. This is correct per the design's
  "every receipt is its own lot" rule (no merging into an existing `InventoryItem`), but
  nothing warns the operator the way `Census/NewOpeningBalance` warns with its "already
  recorded at this slot" list. A future pass could add a similar warning listing recent
  receipts at the same location/PO.

## Session log

- **2026-08-08**: Surveyed Phases 0–2 (all done, no gaps found). Read current `ReservationService`,
  entity models, `Program.cs`, `MaterialLedgerDbContext`, test conventions to ground the Phase 3 plan
  in actual code rather than the Phase 2 plan's text (which had already drifted slightly from what was
  built — e.g. `InventoryQueryService`'s real implementation splits the query differently than its own
  plan's code sample). Wrote `docs/superpowers/plans/2026-08-08-material-ledger-phase3.md`. Executing
  it via `superpowers:subagent-driven-development`, working directly on `master` (confirmed with human
  partner, matching Phases 0–2 convention — no worktree). Ledger:
  `.superpowers/sdd/2026-08-08-material-ledger-phase3/progress.md`.
- **2026-08-08 (Task 1 done)**: `CloseoutService` (`MarkReservationAwaitingCloseout` +
  `CloseoutConsumption`, all 4 outcomes) implemented and reviewed clean (spec ✅, 0
  Critical/Important, 3 Minor deferred — see ledger). 13/13 tests passing. Commits `5e9835d`..`d093003`.
  One real bug caught during review: the plan's own `UsableDropCreated` test asserted
  `MaterialTransactions.SingleAsync(x => x.TransactionType == ...)` without scoping to the reservation
  under test, so it collided with `Consumption`-type rows written by other tests sharing the same
  test-class database — fixed by scoping both queries to `MaterialReservationId` (test-only fix, no
  production code changed). Worth remembering: test DB is shared across all test *methods* within one
  xUnit class (`IClassFixture`) — always scope assertions to the entity under test, never assert on an
  unscoped `TransactionType`/enum-only predicate.
- **2026-08-08 (Task 2 done)**: `MaterialExceptionService` (`ScanAsync`, `GetOpenExceptionsAsync`,
  `GetRecentlyResolvedAsync`, `ResolveMaterialExceptionAsync`) implemented and reviewed clean after one
  fix round. 8/8 tests passing; full integration suite re-run at 66/66, no regressions. Commits
  `a633dc8`..`c4f7e52`. Second instance of the same shared-test-DB bug class as Task 1, worth
  internalizing as a standing rule for any future test in this codebase: when a test asserts an
  *aggregate* result over a detector/scanner that scans the whole DB (not just entity-scoped
  `SingleAsync`/`CountAsync` queries), every other test that creates a re-triggerable condition and
  then never "closes the loop" (e.g. resolves an exception without re-scanning afterward) can leak
  state that inflates that aggregate count in a later test — and this only shows up if xUnit happens to
  order test methods unfavorably, so it can pass 100% of the time locally and still be a real bug.
  Fixed by having every test that creates such a condition finish by consuming it (call the scanner
  again after resolving).
- **2026-08-08 (Task 3 done)**: `Pages/Closeout/{Index,Details,Success}` implemented and reviewed
  clean after one fix round. `dotnet build` clean (0 errors/warnings). Commits `ac9a500`..`d358abd`.
  **Important, applies to any future Razor Pages form in this codebase (Task 4's Exceptions page had
  the identical bug and was fixed pre-emptively in the plan doc before dispatch):** never combine
  `asp-page-handler`/`asp-route-id` tag helpers with an explicit `@Html.AntiForgeryToken()` — the
  `FormTagHelper` auto-injects its own token whenever any `asp-*` attribute is present on a
  `method="post"` form, so pairing it with an explicit token double-emits `__RequestVerificationToken`
  and breaks every submit. This is now the THIRD time this exact defect has been hit in this codebase
  (after commits `24c1f54` and `841a4c2` in Phase 2). The correct pattern, established in
  `Reservations/Details.cshtml`: raw `action="?handler=X"` query string + a single explicit
  `@Html.AntiForgeryToken()`. A route id needed by the handler travels via the page's own URL (for
  `@page "{id:long}"` detail pages) or a plain hidden `<input>` field named to match the handler's
  parameter (for list pages with no id in their own URL) — never `asp-route-id` on the form itself.
  The Phase 3 plan's Global Constraints section has been corrected to state this explicitly for any
  future phase.
- **2026-08-08 (Task 4 done)**: `Pages/Exceptions/Index` implemented, reviewed clean on the first
  pass (no fix round needed) — the antiforgery fix from Task 3 was applied pre-emptively to the plan
  doc before this task's brief was even extracted, so it never recurred. `dotnet build` clean.
  Commits `d358abd`..`1ef7f68`. **Phase 3 (all 4 code/schema tasks) is now implementation-complete**;
  only Task 5 (grants file, no code) and Task 6 (manual live pilot) remain, followed by the final
  whole-branch review.
- **2026-08-09 (ReceiveMaterial branch, worktree `receive-material`)**: Implemented and reviewed
  `ReceiveMaterialService` (`MaterialLedger.Data/Receiving/ReceiveMaterialService.cs`) — write
  procedure + 8 integration tests, same transactional/idempotency shape as `OpeningBalanceService` —
  plus `docs/schema/material-permissions-v5.sql` (grants `SELECT, INSERT` on `material.SheetLots` to
  `materialledger_app`; **not yet applied to the real Forge DB** — outstanding manual prerequisite,
  same one-file-per-phase convention as v1–v4, before this feature will actually work against the live
  database) and `Pages/Receiving/{NewReceipt,Success}`, reached via a new "Receive material" nav link
  (reuses the shared `Census/SelectSlot` picker, per the approved design). Commits `7e9a296`..`158bc32`.
  A consolidated final-review fix pass then addressed: this `PROGRESS.md` (this entry, the
  write-services/pages list update, and the "Known follow-ups" subsection above), a corrected claim in
  `docs/superpowers/specs/2026-08-09-receive-material-design.md`'s Non-goals section, the new
  "Receive material" nav entry point in `Shared/_Layout.cshtml` plus a neutralized `Census/SelectSlot`
  heading (no longer reads as census-only), named-argument construction of `ReceiveMaterialRequest` in
  `NewReceipt.cshtml.cs` (was 15 positional args including five consecutive `string?` — transposition
  risk with no test coverage), and `maxlength`/`min="0"` attributes on `NewReceipt.cshtml`'s new inputs
  to match DB column limits and reject negative cost client-side. Commit `5cf9dc5` (approximate —
  this is a self-referencing hash; the actual final commit hash may differ by one amend since writing
  the hash into the file necessarily changes the commit it describes, check `git log -1` for the
  precise value).
  Full suite: 79/80 passing (one pre-existing, expected, environment-gated failure —
  `MaterialTransactionPermissionsTests.MaterialledgerApp_CannotUpdateMaterialTransactions`, needs
  `MATERIALLEDGER_TEST_APP_CONNECTION`, not set in this dev environment — not a regression).
- **2026-08-10 (Shop-floor workflow redesign, Tasks 1–2)**: Began implementation from
  `.hermes/plans/2026-08-10_020904-shop-floor-workflow-redesign.md`. Added the source-level
  `ShopFloorWorkflowPresentationTests` contract and updated the overview presentation expectation;
  these tests intentionally remain red until the associated UI tasks are implemented (current red
  coverage: physical action labels, separated receipt/count routes, one closeout outcome form, and
  non-technical queue labels). Added `ReservationWorkflowDisplay` with explicit mappings for all eight
  `ReservationStatus` values and semantic CSS classes; focused display mapping tests pass (8/8) and
  `dotnet build MaterialLedger.sln --no-restore --verbosity minimal` completed with 0 warnings and
  0 errors. No schema, production data, Forge deployment, or physical-pilot activity occurred.
- **2026-08-10 (Shop-floor workflow redesign, Tasks 3–8):** Completed the source-only redesign from
  `.hermes/plans/2026-08-10_020904-shop-floor-workflow-redesign.md`. The control board and role-aware
  work queues now use SQL-backed persisted data and central `ReservationWorkflowDisplay` labels;
  canonical inventory availability is shared by search/count and the selected-hold summary; receiving
  and stock count have separate entry routes; reservation holds and details use plain work references
  without fabricated lookup data; closeout uses one protected outcome decision flow; reservation and
  closeout queue filters persist across details/forms/errors/PRG; and the shared layout has accessible
  operator-language navigation, feedback, focus treatment, and responsive table behavior. Task 8
  presentation contracts cover sidebar/light focus contrast, route/role gating, and overflow behavior.
  Canonical validation: `dotnet build MaterialLedger.sln
  --no-restore --verbosity minimal` completed with 0 warnings/0 errors, and the full suite passed
  155/155 with the disposable `MaterialLedgerTest` database provisioned through the dedicated test
  principal. The local LAN preview was launched at `http://10.10.100.135:8091/Login` against the
  existing MaterialLedger database using the dedicated preview principal. No production schema/data
  change, Forge deployment, or physical-pilot activity occurred; the dedicated test principal received
  the verified disposable-database permission required for the integration suite. The redesign remains
  uncommitted for review.
- **2026-08-10 (cutting-wide terminology/control board):** Replaced the user-facing `Work now` label with `Cutting workflow`; replaced laser-specific navigation, queue, role, program-record, and seeded-location labels with cutting-wide language; and changed control-board Ready for cutting / Record cut result counts and links to include all equipment rather than only `Laser`. Existing legacy display names are updated only when they exactly match the prior seeded names, preserving custom names. The persisted `LaserProgramRecordId` field and explicit `Laser` equipment option remain technical/selection details, while their operator-facing record label is now generic. Added `CuttingScopeTerminologyTests`, updated the existing all-equipment count contract, and added a legacy-location-label update test. Canonical validation: terminology test 2/2, location-seeder test 3/3, full suite 158/158, and `dotnet build MaterialLedger.sln --no-restore --nologo` completed with 0 warnings/0 errors. No schema, production data, Forge deployment, or physical-pilot activity occurred. The current source is running as a local LAN preview at `http://10.10.100.135:8091/Login` with the dedicated preview principal and preserved data-protection keys; `/Login` returned HTTP 200 from localhost, the LAN address, and Forge.
- **2026-08-10 (catalog-only material entry):** Replaced free-form new-material entry on the control-board Add a sheet dialog, opening-balance count, and receiving form with a two-step material family/grade/finish then thickness selection backed only by active catalog specs; the thickness choice now displays the catalog's gauge label alongside its decimal inch thickness. Each entry handler independently rejects a missing or inactive spec ID. Added the Programmer/Manager-gated Material catalog page for creating a material deliberately, and linked it from each entry form and navigation. Focused quick-sheet presentation tests passed 2/2 after a RED 1/2 result; `dotnet build MaterialLedger.sln --no-restore --verbosity minimal` completed with 0 warnings/0 errors. Full suite remains 159/160: the same unrelated `ReservationHoldPresentationTests.Hold_form_centers_the_selected_item_and_one_readable_work_reference` expects superseded raw `@Model.Item?.AvailableQuantity` markup, while the page correctly uses `DisplayFormat.Quantity`. No schema, production data, Forge deployment, or physical-pilot activity occurred.
