# Authenticated UI Review Implementation Progress

Source review: `/home/aj/extracted/2026-08-11-materialledger-ui-review/authenticated/`

## Scope

Implement actionable usability and responsive-layout improvements found in the authenticated review captures for Programmer, Operator, and Manager roles. Preserve existing role permissions and workflow behavior.

## Findings triage

| Priority | Finding | Evidence | Status |
|---|---|---|---|
| High | Mobile inventory, hold/approval, and closeout queues expose dense desktop tables that are difficult to scan and act on from a narrow viewport. | Mobile captures contain full table headers and rows (`operator-mobile-inventory.txt`, role-equivalent workflow pages). | Implemented — narrow layouts now show task-oriented cards while desktop retains tables. |
| High | Authenticated desktop rail needs clearer section separation between brand and navigation groups. | Desktop captures; `layout.css` adds rail dividers. | Implemented — CSS and presentation assertions validated. |
| Medium | The catalog displays every thickness in every material family at once on mobile, creating an excessively long scanning path. | `manager-mobile-catalog.txt` lists 11 material types and all thicknesses serially. | Implemented — mobile presents each family as a collapsed native disclosure with its thickness count; desktop retains the complete table. |
| Medium | Control-board Recent Work remains a wide table on mobile. | `programmer-mobile-control-board.txt` ends with seven table columns. | Implemented — mobile presents recent-work cards while desktop retains the table. |
| Medium | Programmer desktop captures report one console error per reviewed page; mobile and other roles do not. | `capture-summary.txt`, rows 1–6. | Implemented — the capture runner accumulated the first desktop context error across its six pages. That first context was the only one to request the browser default `/favicon.ico`, which was absent. The shared layout now declares an existing SVG favicon. |

## Change log

### 2026-08-12 — intake

- Confirmed source artifacts include 36 authenticated captures: six pages, three roles, desktop and mobile.
- Preserved pre-existing working-tree changes rather than overwriting them:
  - `MaterialLedger.Web/wwwroot/css/layout.css`: rail divider styling.
  - `MaterialLedger.Web/wwwroot/css/responsive.css`: initial mobile-card styling.
  - `MaterialLedger.Tests/NavigationPresentationTests.cs`: rail-divider assertions.
- Added responsive card branches for inventory search, material-hold queues, and cut-result queues. Desktop tables remain the desktop presentation; card branches reuse the same role checks, routes, statuses, and action labels.
- Added a presentation regression test that asserts both desktop and mobile branches plus the visibility contract.
- Kept and validated the pre-existing rail-divider CSS and assertions.
- Reworked the mobile material catalog so each family exposes a collapsed native thickness disclosure with its option count. The complete table remains unchanged on desktop; opening the disclosure retains all thickness rows and the existing `Add thickness` action.
- Added a presentation regression assertion for the catalog’s desktop/mobile visibility contract and disclosure content.
- Replaced the control-board Recent Work mobile table with compact cards that retain material item, workflow, material, location, work reference, equipment, and timestamp; desktop retains the complete table.
- Added a presentation regression assertion for the Recent Work desktop/mobile visibility contract and card content.
- Diagnosed the repeated Programmer desktop console count as a capture-runner artifact: errors were accumulated for the desktop browser context, so the initial missing browser-default favicon request appeared against every one of its six captured pages. Added an explicit, verified SVG favicon to the shared layout.
- Added a regression assertion that the authenticated layout references the existing favicon asset.
- Ran `dotnet test ... --filter FullyQualifiedName~NavigationPresentationTests --no-restore`: 11 passed.
- Ran the full suite after these changes: build completed, 109 tests passed, and 303 integration tests failed before execution because `MATERIALLEDGER_TEST_ADMIN_CONNECTION` is unavailable. No application-test regression was indicated; this environment prerequisite prevents database-backed verification.

## Verification record

- `git diff --check`: passed.
- Focused presentation suite: passed (9/9).
- Full suite: blocked by missing SQL Server administrative test connection. The suite deliberately provisions/drops `MaterialLedgerTest`; no credential was added or inferred.
