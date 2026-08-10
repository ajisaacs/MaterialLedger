# Split rack/slot picker in Add-a-sheet dialog

## Problem

`MaterialLedger.Web/Pages/Index.cshtml`'s "Add a sheet" dialog (the control-board quick-entry form for recording a sheet already on the shop floor) has a single `<select>` for `NewSheet.LocationId` that lists every active rack slot flattened into one option per slot, e.g. `"Rack A — Shelf 3"`. As racks are confirmed with more slots (see `docs/superpowers/specs/2026-08-10-add-slots-to-confirmed-rack-design.md`), this list keeps growing and becomes unwieldy to scan.

## Approach

Split the single flat select into two dependent selects, mirroring the existing Material family → Thickness pattern already present in the same dialog (`Index.cshtml` lines ~50–67):

1. **Rack select** (`id="new-sheet-rack"`, unbound — UI filter only) — one option per distinct `ParentInventoryLocation` found in `Model.MaterialLocations`, ordered by the rack's `SortOrder`. Label reads "Rack".
2. **Slot select** (`id="new-sheet-slot"`, bound via `asp-for="NewSheet.LocationId"`) — starts `disabled`. Iterates the existing `Model.MaterialLocations` list unchanged; each `<option>` gets `data-rack-id="@location.ParentInventoryLocationId"` and its text becomes just `@location.LocationCode` (the rack name no longer needs repeating since it's already chosen separately). Label reads "Slot".

JS behavior (adapted from the existing material/thickness filter script already in `Index.cshtml`):
- On rack change: show/enable only `<option>`s whose `data-rack-id` matches the selected rack, hide/disable the rest, reset the slot select's value, and disable the slot select whenever no rack is chosen.
- On page load, if the slot select already has a value (e.g. after a failed POST redisplays the form), pre-select the matching rack and run the filter without resetting the slot value — same pattern the material/thickness script uses via `thickness.options[thickness.selectedIndex]`.

## Non-goals / no backend changes

- `IndexModel.MaterialLocations`, `LoadNewSheetOptionsAsync`, and `OnPostAddSheetAsync`'s validation (`MaterialLocations.All(x => x.InventoryLocationId != NewSheet.LocationId)`) are unchanged — they already operate on the slot's `InventoryLocationId`, which is exactly what the (still bound) slot select posts.
- Placeholder option text changes from "Choose confirmed rack slot" to "Choose rack" / "Choose slot" on the respective selects.
- No changes to `Receiving/SelectLocation.cshtml` or `Census/SelectSlot.cshtml` — those already present rack/slot as a table, not a dropdown, and are out of scope.

## Testing

Pure Razor/JS UI change; `NewSheet.LocationId` posts the same value shape as before, so no new C# logic needs unit tests. Verify manually in the browser:
- Dialog opens with rack select populated and slot select disabled.
- Choosing a rack enables the slot select and filters it to that rack's slots only.
- Switching racks resets the slot selection.
- Submitting with a valid rack+slot still creates the sheet (existing `OnPostAddSheetAsync` flow).
- Submitting with no slot chosen still shows the existing "Choose an active material slot." error and redisplays the dialog with selections intact.
