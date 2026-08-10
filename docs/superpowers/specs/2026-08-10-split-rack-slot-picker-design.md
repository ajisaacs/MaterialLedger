# Split rack/slot picker in Add-a-sheet dialog, and fix dependent-select filtering

## Problem

`MaterialLedger.Web/Pages/Index.cshtml`'s "Add a sheet" dialog (the control-board quick-entry form for recording a sheet already on the shop floor) has a single `<select>` for `NewSheet.LocationId` that lists every active rack slot flattened into one option per slot, e.g. `"Rack A — Shelf 3"`. As racks are confirmed with more slots (see `docs/superpowers/specs/2026-08-10-add-slots-to-confirmed-rack-design.md`), this list keeps growing and becomes unwieldy to scan.

## Approach

Split the single flat select into two dependent selects, mirroring the existing Material family → Thickness pattern already present in the same dialog (`Index.cshtml` lines ~50–67):

1. **Rack select** (`id="new-sheet-rack"`, unbound — UI filter only) — one option per distinct `ParentInventoryLocation` found in `Model.MaterialLocations`, ordered by the rack's `SortOrder`. Label reads "Rack".
2. **Slot select** (`id="new-sheet-slot"`, bound via `asp-for="NewSheet.LocationId"`) — starts `disabled`. Iterates the existing `Model.MaterialLocations` list unchanged; each `<option>` gets `data-rack-id="@location.ParentInventoryLocationId"` and its text becomes just `@location.LocationCode` (the rack name no longer needs repeating since it's already chosen separately). Label reads "Slot".

JS behavior (adapted from the material/thickness filter script already in `Index.cshtml`, using the corrected detach/re-append technique described below rather than its old hidden/disabled toggle):
- On rack change: rebuild the slot select's options to only those whose `data-rack-id` matches the selected rack, reset the slot select's value, and disable the slot select whenever no rack is chosen.
- On page load, if the slot select already has a value (e.g. after a failed POST redisplays the form), pre-select the matching rack and run the filter without resetting the slot value — same pattern the material/thickness script uses via `thickness.options[thickness.selectedIndex]`.

## Second problem: dependent selects don't actually hide filtered-out options

This app has four dependent Material family → Thickness selects, all using an identical script (`Index.cshtml`'s "Add a sheet" dialog, `Census/NewOpeningBalance.cshtml`, `PurchaseOrders/Create.cshtml`'s PO lines, `Receiving/NewReceipt.cshtml`). Each one filters by toggling `option.hidden = !matches` (plus `option.disabled`) on the thickness `<option>` elements. `hidden` on `<option>` inside a `<select>` has inconsistent browser support, so non-matching thicknesses can still show up in the dropdown (disabled/grayed but visible) instead of being removed from the list — which is the bug being reported.

**Fix:** replace the hide/disable toggle with actually detaching non-matching `<option>` elements from the DOM and re-appending only the matching ones, in all four occurrences of this script. This is unambiguous cross-browser (an element not in the DOM cannot render in the dropdown) and keeps the same `data-material-key` matching logic and overall structure — only the filtering mechanics change:

- On page load, capture the full set of thickness `<option>` elements (excluding the placeholder) once, in original order.
- `filterThicknesses(reset)`: remove all non-placeholder `<option>`s currently in the select, then re-append (from the captured set, in original order) only those whose `data-material-key` matches the selected material. Keep the select's `disabled` state tied to whether a material is chosen. On `reset`, clear the value; otherwise try to restore the previously selected value (it will naturally be blank if that option got filtered out).

The new rack/slot select from the section above will use this corrected technique from the start (not the old hidden/disabled toggle), so it doesn't inherit the same bug.

## Non-goals / no backend changes

- `IndexModel.MaterialLocations`, `LoadNewSheetOptionsAsync`, and `OnPostAddSheetAsync`'s validation (`MaterialLocations.All(x => x.InventoryLocationId != NewSheet.LocationId)`) are unchanged — they already operate on the slot's `InventoryLocationId`, which is exactly what the (still bound) slot select posts.
- Placeholder option text changes from "Choose confirmed rack slot" to "Choose rack" / "Choose slot" on the respective selects.
- No changes to `Receiving/SelectLocation.cshtml` or `Census/SelectSlot.cshtml` — those already present rack/slot as a table, not a dropdown, and are out of scope.
- The thickness-filter fix touches only the `<script>` blocks in the four affected pages — the bound field (`ExistingMaterialSpecId` / `Lines[i].ExistingMaterialSpecId`), the C# models, and the server-side validation in each page are unchanged.

## Testing

Pure Razor/JS UI change; `NewSheet.LocationId` posts the same value shape as before, so no new C# logic needs unit tests. Verify manually in the browser:
- Dialog opens with rack select populated and slot select disabled.
- Choosing a rack enables the slot select and filters it to that rack's slots only.
- Switching racks resets the slot selection.
- Submitting with a valid rack+slot still creates the sheet (existing `OnPostAddSheetAsync` flow).
- Submitting with no slot chosen still shows the existing "Choose an active material slot." error and redisplays the dialog with selections intact.
- On each of the four material/thickness dialogs (Index "Add a sheet", NewOpeningBalance, PurchaseOrders/Create — including adding a second PO line, NewReceipt): choosing a material shows only that material's thicknesses in the dropdown (not just disabled/grayed ones), switching materials updates the list, and submitting still saves correctly.
