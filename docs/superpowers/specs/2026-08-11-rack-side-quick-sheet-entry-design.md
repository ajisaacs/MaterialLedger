# Rack-side quick sheet entry (no-reload "Add a sheet")

## Problem

The Control board's "Add a sheet" dialog (`MaterialLedger.Web/Pages/Index.cshtml`, handler `IndexModel.OnPostAddSheetAsync`) is meant for recording sheets that are already on the shop floor, rack by rack. Today, submitting it does a normal form POST that redirects to `/Census/Success`, a separate page showing the item code with a link back to `/Census/SelectSlot`. To record a second sheet at the same rack, the operator has to navigate back to the Control board, reopen the dialog, and re-pick everything — rack, slot, material, thickness, size — from scratch.

In practice this makes entering a stack of sheets at one rack (e.g. standing at the East drop rack with several slots to fill) take roughly a minute per item, almost all of it in navigation and re-selecting the same rack over and over rather than in the actual data entry.

## Approach

Turn `OnPostAddSheetAsync` into a JSON-returning handler and drive the dialog with `fetch()` so an "Add" never navigates away or closes the dialog:

1. **Rack stays put, everything else resets.** After a successful add, the rack selection is left alone; slot, material, thickness, width, length, notes, and the quarantine checkbox are cleared, and focus moves to the slot select so the next item can be entered immediately.
2. **A running list, not a redirect.** Each successful add appends a row to a list inside the dialog (item code, slot, material/thickness, size) so the operator can glance back and confirm what's gone in during this rack visit, without leaving the page.
3. **Errors stay in place.** A validation error (inactive slot, retired material) is shown inline; entered values are left exactly as typed so only the wrong field needs fixing.

This keeps the existing plain-Razor-Pages-plus-vanilla-JS architecture (the same pattern `wwwroot/js/dependent-select.js` already established for the rack→slot and material→thickness pairs) rather than introducing a JS framework or an iframe-submit trick.

## Backend: `IndexModel.OnPostAddSheetAsync`

- Validation logic is unchanged (slot must appear in `ActiveMaterialSlotQuery`, material spec must be active), but on failure the handler returns `JsonResult({ success: false, error: "..." })` instead of setting `NewSheetError` and calling `Page()`.
- On success, it calls `OpeningBalanceService.RecordOpeningBalanceAsync` exactly as today (unchanged: `InventoryItemKind.FullSheet`, quantity `1`, `ShapeClassification.Rectangular`), then returns:
  ```json
  { "success": true, "itemCode": "SHEET-1042", "slotCode": "A3", "materialDisplayName": "16GA CRS", "usableWidthIn": 48, "usableLengthIn": 96 }
  ```
- The correlation ID passed to `RecordOpeningBalanceAsync` is generated **inside the handler** with `Guid.NewGuid()` on every call, rather than bound from a client-supplied hidden field as it is today. Each add is a distinct logical operation now that the dialog stays open across many of them; reusing one ID across adds would make the idempotency guard (`OperationLogGuard`) treat the second add as a conflicting resubmission of the first, since its payload differs from the first's. Client-side ID generation (`crypto.randomUUID()`) is avoided because it requires a secure context, which a plain-HTTP shop-floor LAN deployment cannot guarantee. Double-submit-by-double-click is instead prevented by disabling the submit button while a request is in flight.
- `NewSheet.CorrelationId` (the bound property and its hidden `<input>`) is removed, since nothing generates or needs it client-side anymore.
- `NewSheetError` and `ShowAddSheetDialog` are removed — nothing redirects back to `Index` anymore, so there's no redisplay case left for them to serve.
- `Census/Success` and `Census/SelectSlot` are untouched; they remain reachable from `Census/NewOpeningBalance.cshtml.cs`'s own flow.

## Frontend: the dialog

- The form's `submit` event is intercepted (`preventDefault`) and replaced with `fetch('?handler=AddSheet', { method: 'post', body: new FormData(form) })`. `FormData` picks up the existing `@Html.AntiForgeryToken()` hidden field automatically, so no extra antiforgery plumbing is needed.
- While a request is in flight, the submit button reads "Adding…" and is disabled.
- **On success (`{ success: true, ... }`):**
  - Append a row to a session list rendered inside the dialog, e.g. `SHEET-1042 — Slot A3 — 16GA CRS — 48 × 96`.
  - Call the existing `dependent-select.js` reset path for the rack→slot pair, but only reset the **slot** side (rack selection is left as-is) — this reuses the same detach/re-append filtering already in place, just triggered without changing the rack trigger's value.
  - Reset material→thickness the same way (both sides clear, since there's no "pin the material" requirement here).
  - Clear width, length, notes, and uncheck quarantine.
  - Move focus to the slot select.
  - Re-enable the submit button.
- **On error (`{ success: false, error }`):** show `error` inline using the existing `.form-error` styling, leave all fields (including rack/slot/material) exactly as entered, re-enable the submit button, and leave the session list untouched.
- The session list lives in the DOM only for as long as the dialog is open: closing it (× button or Cancel) and reopening clears the list, so a later, unrelated session starts empty.

## Non-goals

- `Receiving/*` (vendor receiving, which requires a PO number) is a separate flow and is untouched.
- `Census/SelectSlot`, `Census/NewOpeningBalance`, `Census/Success` are untouched — they remain a separate, table-based rack/slot flow for whoever still uses it directly rather than the Control board dialog.
- No inline undo/delete for a row already added in this session — if a mistake is caught, it's corrected afterward through the existing inventory item edit page, same as today.
- No quantity field — the dialog still always records exactly one full sheet per add, matching current behavior.
- `OpeningBalanceService` and its validation are unchanged; only how `IndexModel` talks to it and to the browser changes.

## Testing

- Update `MaterialLedger.Tests/QuickSheetEntryPresentationTests.cs` for the new markup/behavior: assert the fetch-based submit wiring is present, assert `NewSheet.CorrelationId`'s hidden input is gone, assert a session-list container exists in the dialog.
- Add an integration test alongside `MaterialLedger.Tests/ReceiveAllIntegrationTests.cs`'s pattern (`SqlServerTestDatabaseFixture`, handler invoked directly against a constructed `PageContext`/`ClaimsPrincipal`) covering:
  - A successful add returns the expected JSON shape and creates the inventory item.
  - An invalid slot or an invalid/inactive material spec returns a JSON error and creates nothing.
  - **Two consecutive successful adds in the same session both succeed** — proving the per-call server-generated correlation ID doesn't collide and get rejected as a duplicate.
- Manual browser verification: add several sheets to the same rack back-to-back with no page reload; confirm the rack selection persists while slot/material/thickness/size/notes/quarantine reset after each add; confirm the running list grows with each addition; confirm an error (e.g., choosing a slot that's since gone inactive) leaves all entered values in place; confirm closing and reopening the dialog clears the running list.
