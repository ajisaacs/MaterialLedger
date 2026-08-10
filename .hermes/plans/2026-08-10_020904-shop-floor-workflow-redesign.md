# MaterialLedger Shop-Floor Workflow Redesign Implementation Plan

> **For Hermes:** Use subagent-driven-development skill to implement this plan task-by-task.

**Goal:** Make MaterialLedger usable from the laser control by presenting the physical material workflow—find material, hold it for work, and record what remained—instead of exposing the internal reservation state machine as the primary interface.

**Architecture:** Preserve the existing EF Core model, transaction/idempotency services, reservation status enum, authorization policies, routes that external users may already bookmark, and audit trail. Add a thin, operator-facing read model for work queues and use Razor Pages as guided surfaces over the existing write services. User-facing labels become physical/action-oriented while the technical status remains visible in compact detail/history context. This is a source-only plan; a Forge deployment is a separate, explicit release step after local user review.

**Tech Stack:** .NET 8, ASP.NET Core Razor Pages, EF Core 9, SQL Server, xUnit integration tests, hand-authored CSS.

---

## Current context and constraints

- Current source: `/home/aj/src/MaterialLedger`; current branch was `master`, clean, at `04f5c40` when assessed.
- Existing controls are valuable and must remain: database-backed inventory, role-based authorization, serializable/idempotent writes, immutable operations, and distinct material milestones.
- Current application lifecycle remains internally correct:
  `Proposed` → `Approved` → `AwaitingCloseout` → terminal closeout state or exception.
- Do not make a reservation imply material was consumed. Do not make a program/nest history record imply on-hand material.
- Operator-facing wording should be plain while preserving exact technical status in a secondary `Workflow status` field:
  - `Proposed` → `Held — approval needed`
  - `Approved` → `Ready for cutting`
  - `AwaitingCloseout` → `Cut finished — record result`
  - `ClosedConsumed` → `Closed — material consumed`
  - `ClosedReturned` → `Closed — sheet returned`
  - `Exception` → `Material needs review`
- Separate receipt and opening-census paths. They may use the same location picker implementation, but they must have distinct headings, entry links, descriptions, and call-to-action labels.
- Keep internal routes/folders (`/Reservations`, `/Census`, `/Exceptions`) stable for now. Add purpose-specific entry links rather than performing a risky route migration.
- Keep role authorization intact. If a user cannot take an action, do not show a dead-end button. Show read-only work queues only where they give that role useful context.
- Avoid new schema work unless the design review proves it is essential. This redesign should first use current fields and services.
- Do not enter production data or run administrative SQL grants as part of this UI work.

## User-facing acceptance criteria

1. A programmer can start with a job/drawing/nest reference, find suitable inventory, select an item, and make a hold without needing to understand `Proposed`/`Approved` nomenclature.
2. A laser operator can open a short “record cut result” queue and complete one outcome from a single decision flow.
3. Receiving a vendor delivery and counting existing stock are visibly separate tasks.
4. The home page answers “what should I do next?” using real persisted queue counts and direct links, not a neutral list of modules.
5. The application does not invent stock, nest details, expected remainder, job details, or completion status where no canonical data is available.
6. Every existing write remains permission-gated and idempotent, with no change to transaction semantics.

## Design decisions to lock before implementation

These are small, concrete approvals to obtain in the implementation kickoff rather than assumptions to hide in code:

1. **Reservation approval:** Confirm whether a programmer’s hold should automatically become `Approved`, or whether the existing manager approval remains a required control. Default: retain the approval state/control but name it clearly and surface it as a queue, not a surprise secondary screen.
2. **Job/program lookup:** Confirm which canonical source can supply job, drawing, revision, and PEP nest selections to the UI. Default implementation must not fabricate an integration; it can initially allow one required human-readable work reference plus an optional “additional traceability” disclosure.
3. **Operator device:** Confirm target display size and whether the laser-control UI has touch input. Default: desktop/laptop first, controls sized for gloved/touch use where practical.
4. **Receipt location selection:** Confirm whether the established rack/slot model is enough for receiving, or whether a temporary receiving/staging location should be added through the approved location-management process. Do not introduce new locations during this UI scope.

---

### Task 1: Capture current behavior and write behavior-level regression tests

**Objective:** Create tests that constrain the requested operator workflow before changing markup, without treating static-string assertions as complete UI testing.

**Files:**
- Create: `MaterialLedger.Tests/ShopFloorWorkflowPresentationTests.cs`
- Modify: `MaterialLedger.Tests/OverviewPageDesignTests.cs`
- Inspect: `MaterialLedger.Web/Pages/Index.cshtml`, `Shared/_Layout.cshtml`, `Census/SelectSlot.cshtml`, `Receiving/NewReceipt.cshtml`, `Reservations/*.cshtml`, `Closeout/*.cshtml`

**Step 1: Write failing source-level presentation tests**

Add focused tests that assert the planned semantic boundaries in the rendered markup sources:

- the control board has direct actions for `Find material`, `Hold material for work`, and `Record cut result`;
- the receiving entry point points to the receipt path and is not labeled `Opening census`;
- the opening-count entry point has a separate label/description;
- closeout exposes one outcome-selection form/container rather than four independent top-level POST forms;
- the technical strings `Proposed`, `Approved`, and `AwaitingCloseout` are not used as the only user-facing status labels in work-queue pages.

**Step 2: Run the focused test class and confirm red**

Run:

```bash
dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --no-restore --filter FullyQualifiedName~ShopFloorWorkflowPresentationTests --verbosity minimal
```

Expected: failure because the current control board, location picker, and closeout markup do not yet meet the new workflow contract.

**Step 3: Add only the test scaffolding needed for subsequent tasks**

Keep file reads rooted through `AppContext.BaseDirectory`, matching existing presentation tests. Do not add a browser test framework in this task.

**Step 4: Commit the failing characterization/spec tests only if the repository workflow permits an intentional red commit; otherwise retain them locally until Task 6 turns them green.**

Suggested message after green: `test: define shop-floor workflow presentation contract`.

---

### Task 2: Create one shared display mapping for reservation statuses

**Objective:** Centralize human-readable workflow labels and semantic CSS classes so list pages, details, dashboard counts, and closeout use the same language.

**Files:**
- Create: `MaterialLedger.Web/Shared/ReservationWorkflowDisplay.cs`
- Create: `MaterialLedger.Tests/ReservationWorkflowDisplayTests.cs`
- Modify: `MaterialLedger.Web/wwwroot/css/app.css`
- Later consumers: `Pages/Reservations/Index.cshtml`, `Pages/Reservations/Details.cshtml`, `Pages/Closeout/Index.cshtml`, `Pages/Closeout/Details.cshtml`

**Step 1: Write failing unit tests**

Test the exact mapping for every `ReservationStatus`, including the labels listed in Current context and a semantic category such as `pending`, `ready`, `action-needed`, `complete`, and `attention`.

**Step 2: Run the new tests**

```bash
dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --no-restore --filter FullyQualifiedName~ReservationWorkflowDisplayTests --verbosity minimal
```

Expected: fail because `ReservationWorkflowDisplay` does not exist.

**Step 3: Implement the minimal display helper**

Use a static helper, analogous to `InventoryStatusDisplay`, returning a small record:

```csharp
public sealed record ReservationWorkflowDisplayValue(string Label, string CssClass, string Description);
```

Map each enum explicitly with a switch expression. Do not stringify enum values or infer semantics from enum order.

**Step 4: Add restrained CSS**

Add semantic status chips/labels with neutral treatment for unknown/unconfigured states, amber for action needed, red only for actual attention/failure conditions, and green only for confirmed ready/completed conditions.

**Step 5: Run the focused tests and build**

```bash
dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --no-restore --filter FullyQualifiedName~ReservationWorkflowDisplayTests --verbosity minimal
dotnet build MaterialLedger.sln --no-restore --verbosity minimal
```

Expected: focused tests pass; build has 0 warnings and 0 errors.

**Step 6: Commit**

```bash
git add MaterialLedger.Web/Shared/ReservationWorkflowDisplay.cs MaterialLedger.Web/wwwroot/css/app.css MaterialLedger.Tests/ReservationWorkflowDisplayTests.cs
git commit -m "feat: add plain-language reservation workflow statuses"
```

---

### Task 3: Build a data-backed “work now” read model

**Objective:** Make the home page and queues show actionable work rather than generic aggregate counts.

**Files:**
- Create: `MaterialLedger.Web/Services/WorkQueueQueryService.cs`
- Create: `MaterialLedger.Tests/WorkQueueQueryServiceTests.cs`
- Modify: `MaterialLedger.Web/Program.cs`
- Modify: `MaterialLedger.Web/Pages/Index.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Index.cshtml`

**Step 1: Write failing integration tests against the existing SQL Server fixture**

Cover at least:

- approved laser reservation appears in `ReadyForCutting`;
- awaiting-closeout laser reservation appears in `CutResultNeeded`;
- proposed reservation appears in `ApprovalNeeded` only;
- terminal reservations do not inflate active queues;
- available-material count still excludes stale and quarantined inventory, using the same canonical rules as `InventoryQueryService.SearchAsync`.

Scope every assertion to created test entities; the suite shares a fixture database across methods.

**Step 2: Run the focused tests and confirm red**

```bash
dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --no-restore --filter FullyQualifiedName~WorkQueueQueryServiceTests --verbosity minimal
```

Expected: compile/test failure because the query service does not exist.

**Step 3: Implement the query service**

Create explicit, read-only projections for:

- available material count;
- holds needing approval;
- material ready for cutting;
- cut results awaiting operator recording;
- open attention items;
- a bounded recent-work list (for example 10 rows) with material code, material display name, location, human-readable work reference, equipment, workflow label, and the timestamp relevant to that state.

Do not alter `ReservationService`, `CloseoutService`, models, or the existing audit data. Do not compute availability from cut history.

**Step 4: Register and use the service**

Register it as scoped in `Program.cs`; change `IndexModel` to consume it instead of maintaining its own ad hoc independent count queries.

**Step 5: Replace the home-page composition**

Keep it a control board, but replace equal-weight module cards with:

- one prominent next-action section based on role;
- direct links to the filtered queue surfaces;
- real count cards for “Ready for cutting,” “Record cut result,” and “Needs attention”;
- a compact recent/current work table below the actions.

Do not show an action that the logged-in role cannot perform.

**Step 6: Run focused tests, build, and inspect source changes**

```bash
dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --no-restore --filter FullyQualifiedName~WorkQueueQueryServiceTests --verbosity minimal
dotnet build MaterialLedger.sln --no-restore --verbosity minimal
git diff --check
```

**Step 7: Commit**

```bash
git add MaterialLedger.Web/Services/WorkQueueQueryService.cs MaterialLedger.Web/Program.cs MaterialLedger.Web/Pages/Index.cshtml.cs MaterialLedger.Web/Pages/Index.cshtml MaterialLedger.Tests/WorkQueueQueryServiceTests.cs
git commit -m "feat: make the control board actionable by workflow state"
```

---

### Task 4: Separate receipt and opening-count entry points

**Objective:** Remove the conceptual collision between receiving a delivery and recording an opening balance while preserving shared location selection safely.

**Files:**
- Create: `MaterialLedger.Web/Pages/Receiving/SelectLocation.cshtml`
- Create: `MaterialLedger.Web/Pages/Receiving/SelectLocation.cshtml.cs`
- Create: `MaterialLedger.Tests/ReceivingEntryPresentationTests.cs`
- Modify: `MaterialLedger.Web/Pages/Census/SelectSlot.cshtml`
- Modify: `MaterialLedger.Web/Pages/Census/SelectSlot.cshtml.cs` only if a clearer view-model name is needed
- Modify: `MaterialLedger.Web/Pages/Shared/_Layout.cshtml`
- Modify: `MaterialLedger.Web/Pages/Index.cshtml`

**Step 1: Write failing tests**

Assert that:

- “Receive delivery” links to `/Receiving/SelectLocation`;
- “Count existing material” links to `/Census/SelectSlot`;
- the receiving picker does not expose “Record opening balance”; and
- the count picker does not present receipt as a sibling option.

**Step 2: Run and confirm red**

```bash
dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --no-restore --filter FullyQualifiedName~ReceivingEntryPresentationTests --verbosity minimal
```

**Step 3: Add a receipt-only location picker**

Reuse the exact confirmed-slot selection query currently used by `Census/SelectSlotModel`, but give the new page receipt-specific heading/copy and one action: `Receive delivery here`.

Extract a shared read-only location query/helper only if duplication cannot be avoided cleanly. Do not add another location table or new location state.

**Step 4: Make census single-purpose**

Rename its page heading and action text to “Count existing material” and “Record counted balance.” Remove the receipt action from its table.

**Step 5: Update role-aware navigation and control-board links**

For Programmer/Manager users, show separate items:

- `Receive delivery`
- `Count existing material`

Keep them under Inventory/support work rather than alongside laser closeout actions.

**Step 6: Verify and commit**

```bash
dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --no-restore --filter FullyQualifiedName~ReceivingEntryPresentationTests --verbosity minimal
dotnet build MaterialLedger.sln --no-restore --verbosity minimal
git add MaterialLedger.Web/Pages/Receiving/SelectLocation.cshtml MaterialLedger.Web/Pages/Receiving/SelectLocation.cshtml.cs MaterialLedger.Web/Pages/Census/SelectSlot.cshtml MaterialLedger.Web/Pages/Census/SelectSlot.cshtml.cs MaterialLedger.Web/Pages/Shared/_Layout.cshtml MaterialLedger.Web/Pages/Index.cshtml MaterialLedger.Tests/ReceivingEntryPresentationTests.cs
git commit -m "feat: separate delivery receipt from existing-stock count"
```

---

### Task 5: Replace raw reservation entry with a guided hold flow

**Objective:** Allow a programmer/manager to select available material and record the minimum trustworthy work context without confronting a wall of internal identifiers.

**Files:**
- Modify: `MaterialLedger.Web/Pages/Inventory/Search.cshtml`
- Modify: `MaterialLedger.Web/Pages/Inventory/Search.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Reservations/Create.cshtml`
- Modify: `MaterialLedger.Web/Pages/Reservations/Create.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Reservations/Details.cshtml`
- Create: `MaterialLedger.Tests/ReservationHoldPresentationTests.cs`
- Optional only after confirmed canonical integration: `MaterialLedger.Web/Services/<CanonicalWorkLookupService>.cs`

**Step 1: Write failing presentation/handler tests**

Cover:

- search result action uses `Hold for work`, not `Reserve`;
- selected inventory header contains code, material, available quantity, usable size, and location;
- exactly one human-readable `Work reference` is shown in the default form;
- optional structured fields are inside a collapsed `Additional traceability` disclosure;
- a blank work reference is handled deliberately: either required by service validation or explicitly allowed and labeled `Unassigned material hold`.

Do not add a fake lookup test for ShopOps/PEP/LaserDashboard.

**Step 2: Run focused test and confirm red.**

**Step 3: Change default form vocabulary and layout**

- Retitle page to `Hold material for work`.
- Make the item summary visually prominent.
- Group fields as `Work reference`, `Equipment`, and (when useful) `Expected remainder`.
- Move `ShopOpsJobId`, drawing ID/revision, PEP nest year/name, and laser-program ID below `<details>` with contextual descriptions.
- Preserve all existing model bindings and service request fields; this is a default-path UI simplification, not data loss.
- Keep manual expected-remainder entry explicitly marked as an operator estimate. Do not imply OpenNest integration exists.

**Step 4: Improve post-create details**

On reservation detail, show the plain-language state, next responsible role/action, and an audit/technical-details disclosure for raw IDs and technical status.

**Step 5: Perform canonical integration decision**

If user approves an existing source for work lookup, add it in a separate commit after confirming its route/data contract and read permission. The lookup must:

- display canonical source provenance;
- populate fields rather than replacing traceability;
- fail plainly without allowing fabricated selections; and
- leave manual entry available if the canonical service is unavailable.

If no source is approved, stop after the guided manual flow; do not create a shadow job register.

**Step 6: Verify and commit the presentation flow**

```bash
dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --no-restore --filter FullyQualifiedName~ReservationHoldPresentationTests --verbosity minimal
dotnet build MaterialLedger.sln --no-restore --verbosity minimal
git add MaterialLedger.Web/Pages/Inventory/Search.cshtml MaterialLedger.Web/Pages/Inventory/Search.cshtml.cs MaterialLedger.Web/Pages/Reservations/Create.cshtml MaterialLedger.Web/Pages/Reservations/Create.cshtml.cs MaterialLedger.Web/Pages/Reservations/Details.cshtml MaterialLedger.Tests/ReservationHoldPresentationTests.cs
git commit -m "feat: guide material holds around physical work"
```

---

### Task 6: Redesign reservations as role-appropriate work queues

**Objective:** Replace one mixed reservations table with understandable queues and purposeful next actions.

**Files:**
- Modify: `MaterialLedger.Web/Pages/Reservations/Index.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Reservations/Index.cshtml`
- Create: `MaterialLedger.Tests/ReservationQueuePresentationTests.cs`
- Reuse: `MaterialLedger.Web/Shared/ReservationWorkflowDisplay.cs`

**Step 1: Write failing tests**

Assert separate sections/filters for:

- `Approval needed` (manager action only);
- `Ready for cutting` (operator-visible context, programmer/manager action as appropriate);
- `Cut result needed` (operator/manager action link);
- no generic raw `Status` table header as the only explanation of state.

**Step 2: Implement a filtered/read-only model**

Either consume `WorkQueueQueryService` or add an explicit query projection. Avoid duplicating status rules in Razor markup.

**Step 3: Implement the page**

For each row show: material item, material, usable size, location, concise work reference, current plain-language status, duration/timestamp, and one next action. Retain raw identifiers on the detail page, not in the default table.

**Step 4: Verify and commit**

```bash
dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --no-restore --filter FullyQualifiedName~ReservationQueuePresentationTests --verbosity minimal
dotnet build MaterialLedger.sln --no-restore --verbosity minimal
git add MaterialLedger.Web/Pages/Reservations/Index.cshtml MaterialLedger.Web/Pages/Reservations/Index.cshtml.cs MaterialLedger.Tests/ReservationQueuePresentationTests.cs
git commit -m "feat: organize material holds as actionable work queues"
```

---

### Task 7: Turn closeout into one outcome decision flow

**Objective:** Let an operator record the physical outcome of a cut without choosing among four disconnected forms.

**Files:**
- Modify: `MaterialLedger.Web/Pages/Closeout/Index.cshtml`
- Modify: `MaterialLedger.Web/Pages/Closeout/Index.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Closeout/Details.cshtml`
- Modify: `MaterialLedger.Web/Pages/Closeout/Details.cshtml.cs`
- Create: `MaterialLedger.Tests/CloseoutPresentationTests.cs`
- Modify: `MaterialLedger.Web/wwwroot/css/app.css`

**Step 1: Write failing tests**

Test the required user-facing structure:

- closeout queue title is `Record cut result`;
- each row identifies work/material/location and has one primary action;
- the detail page asks `After the cut, what remains?`;
- a single `<form>` contains one mutually exclusive outcome selection and a single final submit;
- width/length/destination fields are only rendered/required for `UsableDropCreated`;
- notes are required for `NeedsReview` if service-level validation already supports/needs this, otherwise add that validation in a separate data-layer task with integration coverage.

**Step 2: Run and confirm red.**

**Step 3: Preserve server-side safety while changing form mechanics**

Do not reduce row-version or correlation-id protections. Keep using raw `action="?handler=..."` plus one explicit antiforgery token per form, following the established Razor Pages restriction in `docs/PROGRESS.md`; do not combine form tag helpers with a second explicit antiforgery token.

Add an `Outcome` bind property and one `OnPost` dispatcher only if it can call the existing specific closeout service operations with their current request shape. Alternatively, retain the specific handlers while using JavaScript-free form buttons that set an outcome value. The selected solution must preserve PRG redirect behavior and clear concurrency errors.

**Step 4: Implement progressive disclosure without client-side dependency**

Use `<details>` or CSS keyed from the selected radio value only if it remains usable without JavaScript. If JavaScript is used for convenience, server-side validation must remain authoritative and the initial non-JavaScript form must still permit each supported outcome.

**Step 5: Improve closeout queue context**

Show material code/spec, usable size, source location, concise work reference, workflow status, and how long the result has been pending. Label the previous `Start job` action as `Mark cut started` only after confirming the operational meaning; otherwise remove it from the closeout screen and retain the existing status control in reservation details.

**Step 6: Run focused tests, full service tests, build, and commit**

```bash
dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --no-restore --filter FullyQualifiedName~CloseoutPresentationTests --verbosity minimal
dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --no-restore --filter FullyQualifiedName~CloseoutServiceTests --verbosity minimal
dotnet build MaterialLedger.sln --no-restore --verbosity minimal
git add MaterialLedger.Web/Pages/Closeout/Index.cshtml MaterialLedger.Web/Pages/Closeout/Index.cshtml.cs MaterialLedger.Web/Pages/Closeout/Details.cshtml MaterialLedger.Web/Pages/Closeout/Details.cshtml.cs MaterialLedger.Web/wwwroot/css/app.css MaterialLedger.Tests/CloseoutPresentationTests.cs
git commit -m "feat: guide operators through one cut-result decision"
```

---

### Task 8: Simplify global navigation and supporting pages

**Objective:** Make global navigation reinforce the real workflow and ensure supporting pages use shop-readable language.

**Files:**
- Modify: `MaterialLedger.Web/Pages/Shared/_Layout.cshtml`
- Modify: `MaterialLedger.Web/Pages/Inventory/Search.cshtml`
- Modify: `MaterialLedger.Web/Pages/Locations/Index.cshtml`
- Modify: `MaterialLedger.Web/Pages/Locations/Details.cshtml`
- Modify: `MaterialLedger.Web/Pages/Exceptions/Index.cshtml`
- Modify: `MaterialLedger.Web/wwwroot/css/app.css`
- Create: `MaterialLedger.Tests/NavigationPresentationTests.cs`

**Step 1: Write failing tests**

Require navigation grouping such as:

- `Work now`: Control board, Find material, Material holds, Record cut result;
- `Inventory setup`: Receive delivery, Count existing material, Material locations;
- `Review`: Attention needed.

Assert that role-hidden actions are absent instead of visible disabled/dead-end links.

**Step 2: Implement minimal navigation changes**

Do not add a sidebar. The existing persistent sidebar already consumes substantial production-table width, so keep it compact and eliminate duplicates rather than expanding it.

Rename supporting surfaces without renaming internals:

- `Material map` → `Material locations`;
- raw exception types gain human explanations on the Attention page;
- status colors in location views use semantic classes rather than inline background styles where practical.

**Step 3: Verify and commit**

```bash
dotnet test MaterialLedger.Tests/MaterialLedger.Tests.csproj --no-restore --filter FullyQualifiedName~NavigationPresentationTests --verbosity minimal
dotnet build MaterialLedger.sln --no-restore --verbosity minimal
git diff --check
git add MaterialLedger.Web/Pages/Shared/_Layout.cshtml MaterialLedger.Web/Pages/Inventory/Search.cshtml MaterialLedger.Web/Pages/Locations/Index.cshtml MaterialLedger.Web/Pages/Locations/Details.cshtml MaterialLedger.Web/Pages/Exceptions/Index.cshtml MaterialLedger.Web/wwwroot/css/app.css MaterialLedger.Tests/NavigationPresentationTests.cs
git commit -m "feat: align navigation with shop-floor material workflow"
```

---

### Task 9: Resolve test-environment readiness, then run the full suite

**Objective:** Restore the integration suite as trustworthy release evidence instead of interpreting permission setup failures as product failures.

**Files:**
- Inspect: `MaterialLedger.Tests/SqlServerTestDatabaseFixture.cs`
- Inspect: `MaterialLedger.Tests/MaterialTransactionPermissionsTests.cs`
- Inspect only, unless user explicitly approves test-environment configuration: local protected test connection configuration
- Do not modify: production SQL schema or Forge permissions without separate user authorization

**Step 1: Confirm which configured connection is used by the test fixture without printing credentials.**

**Step 2: Verify the test administrator has only the permissions needed to create/drop the dedicated throwaway `MaterialLedgerTest` database.**

If the current test connection is an app-level account, configure a separate protected test-admin credential; do not grant broad database-creation capability to the live `materialledger_app` principal.

**Step 3: Run the full suite.**

```bash
dotnet test MaterialLedger.sln --no-restore --verbosity minimal
```

Expected: the full suite runs assertions rather than failing at fixture database creation. Report actual pass/fail totals and any explicitly environment-gated test separately.

**Step 4: Commit only source/test configuration documentation if needed.**

Do not commit credentials.

---

### Task 10: Local LAN review and controlled pilot

**Objective:** Verify the redesigned flows against populated canonical data before release, then validate physical behavior with operators.

**Files:**
- Modify only if findings require a scoped repair: files changed in Tasks 1–8
- Update after completed work: `docs/PROGRESS.md`

**Step 1: Start a temporary, owned LAN preview from the repo**

Use a free port and bind to `0.0.0.0`; use the existing protected configuration/connection string only for the temporary process. Do not change tracked development or production settings.

**Step 2: Browser-check each role**

Using existing authorized dev accounts, inspect each role’s home screen and navigation:

- Programmer: find material → hold for work → observe plain-language status.
- Manager: approve/release a controlled test hold and review attention queue.
- Operator: open record-cut-result queue, select each outcome in a controlled test scenario, and verify the fields shown/hidden make physical sense.

Do not use a real production item for destructive closeout QA.

**Step 3: Validate representative populated states**

Verify available, held, ready-for-cutting, result-needed, quarantined/stale, and no-data states. Inspect browser console for errors, keyboard tab order, desktop table readability, and narrow-screen/table overflow.

**Step 4: Run a controlled live pilot only after UI review approval**

With a real laser workflow and at least one additional equipment type, verify all closeout outcomes where physically appropriate. Reconcile the actual item, location, and remainder against the ledger transaction/audit trail. Keep milestones separate: hold created, approved/ready, cut started if tracked, cut result recorded, and physical material verified.

**Step 5: Update `docs/PROGRESS.md`**

Record only verified source, test, and pilot facts; identify any remaining integration limitations plainly. Do not claim a Forge deployment or physical pilot completed unless they actually did.

---

## Files likely to change

- `MaterialLedger.Web/Pages/Index.cshtml`
- `MaterialLedger.Web/Pages/Index.cshtml.cs`
- `MaterialLedger.Web/Pages/Shared/_Layout.cshtml`
- `MaterialLedger.Web/Pages/Receiving/SelectLocation.cshtml` (new)
- `MaterialLedger.Web/Pages/Receiving/SelectLocation.cshtml.cs` (new)
- `MaterialLedger.Web/Pages/Census/SelectSlot.cshtml`
- `MaterialLedger.Web/Pages/Reservations/Index.cshtml`
- `MaterialLedger.Web/Pages/Reservations/Index.cshtml.cs`
- `MaterialLedger.Web/Pages/Reservations/Create.cshtml`
- `MaterialLedger.Web/Pages/Reservations/Create.cshtml.cs`
- `MaterialLedger.Web/Pages/Reservations/Details.cshtml`
- `MaterialLedger.Web/Pages/Closeout/Index.cshtml`
- `MaterialLedger.Web/Pages/Closeout/Index.cshtml.cs`
- `MaterialLedger.Web/Pages/Closeout/Details.cshtml`
- `MaterialLedger.Web/Pages/Closeout/Details.cshtml.cs`
- `MaterialLedger.Web/Pages/Inventory/Search.cshtml`
- `MaterialLedger.Web/Pages/Locations/Index.cshtml`
- `MaterialLedger.Web/Pages/Locations/Details.cshtml`
- `MaterialLedger.Web/Pages/Exceptions/Index.cshtml`
- `MaterialLedger.Web/Services/WorkQueueQueryService.cs` (new)
- `MaterialLedger.Web/Shared/ReservationWorkflowDisplay.cs` (new)
- `MaterialLedger.Web/Program.cs`
- `MaterialLedger.Web/wwwroot/css/app.css`
- focused test files under `MaterialLedger.Tests/`
- `docs/PROGRESS.md` after verified implementation/pilot facts

## Risks and mitigations

- **User-friendly labels can obscure actual state.** Mitigation: central mapping with raw technical status and audit information retained on details; never change the enum just to change display text.
- **A job/nest selector could become a second, stale source of truth.** Mitigation: do not build it until canonical source, route, read permission, and provenance are confirmed; retain manual traceability fields as a fallback.
- **Streamlining closeout could weaken data controls.** Mitigation: retain server-side outcome validation, correlation IDs, row-version concurrency checks, antiforgery rules, and existing transactional `CloseoutService` calls.
- **Role tailoring can hide needed awareness.** Mitigation: hide unauthorized mutation controls, not essential read-only work context; browser-check all three roles.
- **Integration tests are currently blocked by test DB creation permission.** Mitigation: use the dedicated test administrator only for the throwaway test DB; do not broaden the production app principal.
- **Source-only work can be mistaken for deployment.** Mitigation: keep commits separate and state local review/deployment/pilot status independently.

## Final verification checklist

- [ ] New focused test classes prove the presentation/workflow contract and are green.
- [ ] Full test suite runs against a throwaway test DB and reports real test outcomes.
- [ ] `dotnet build MaterialLedger.sln --no-restore` succeeds with 0 warnings and 0 errors.
- [ ] `git diff --check` is clean.
- [ ] Browser review has run for Programmer, Operator, and Manager using populated test data.
- [ ] Control board and queues use real persisted counts only.
- [ ] Receipt and count-existing-stock are separate workflows.
- [ ] The normal hold path does not require knowledge of raw ShopOps/PEP/LaserDashboard IDs.
- [ ] Closeout retains all four audited outcomes and is understandable from a single decision prompt.
- [ ] Technical status/audit detail remains available without dominating the floor workflow.
- [ ] A LAN preview is reviewed before any Forge deployment.
- [ ] A controlled physical pilot verifies the ledger against observed material movement before calling the redesign operationally complete.
