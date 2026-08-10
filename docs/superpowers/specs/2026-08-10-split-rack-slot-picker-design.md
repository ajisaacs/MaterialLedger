# Split rack/slot picker in Add-a-sheet dialog, and fix dependent-select filtering

## Problem

`MaterialLedger.Web/Pages/Index.cshtml`'s "Add a sheet" dialog (the control-board quick-entry form for recording a sheet already on the shop floor) has a single `<select>` for `NewSheet.LocationId` that lists every active rack slot flattened into one option per slot, e.g. `"Rack A — Shelf 3"`. As racks are confirmed with more slots (see `docs/superpowers/specs/2026-08-10-add-slots-to-confirmed-rack-design.md`), this list keeps growing and becomes unwieldy to scan.

## Approach

Split the single flat select into two dependent selects, mirroring the existing Material family → Thickness pattern already present in the same dialog (`Index.cshtml` lines ~50–67):

1. **Rack select** (`id="new-sheet-rack"`, unbound — UI filter only) — one option per distinct `ParentInventoryLocation` found in `Model.MaterialLocations`, ordered by the rack's `SortOrder`. Label reads "Rack".
2. **Slot select** (`id="new-sheet-slot"`, bound via `asp-for="NewSheet.LocationId"`) — starts `disabled`. Iterates the existing `Model.MaterialLocations` list unchanged; each `<option>` gets `data-rack-id="@location.ParentInventoryLocationId"` and its text becomes just `@location.LocationCode` (the rack name no longer needs repeating since it's already chosen separately). Label reads "Slot".

The filtering behavior (choosing the trigger select narrows the dependent select to matching options, disables the dependent select until a trigger value is chosen, and restores a prior selection on redisplay) is implemented once, in a shared script — see below — rather than written per-page.

## Second problem: dependent selects don't actually hide filtered-out options

This app has four dependent Material family → Thickness selects (`Index.cshtml`'s "Add a sheet" dialog, `Census/NewOpeningBalance.cshtml`, `PurchaseOrders/Create.cshtml`'s PO lines, `Receiving/NewReceipt.cshtml`), each with its own copy-pasted inline script. Every copy filters by toggling `option.hidden = !matches` (plus `option.disabled`) on the thickness `<option>` elements. `hidden` on `<option>` inside a `<select>` has inconsistent browser support, so non-matching thicknesses can still show up in the dropdown (disabled/grayed but visible) instead of being removed from the list — which is the bug being reported.

**Fix:** extract the filtering behavior into one shared, reusable script instead of fixing (and continuing to duplicate) the same logic five times over — the four existing material/thickness pairs plus the new rack/slot pair make this the right moment to de-duplicate rather than copy-paste a fifth variant.

### Shared script: `wwwroot/js/dependent-select.js`

A single global function, loaded as a static asset (this app already serves `wwwroot/css/app.css` the same way — see `Pages/Shared/_Layout.cshtml:7` — and `Program.cs` already calls `app.UseStaticFiles()`):

```js
function initDependentSelect(trigger, dependent, datasetKey) {
    const allOptions = Array.from(dependent.options).filter(option => option.dataset[datasetKey] !== undefined);
    const selected = allOptions.find(option => option.value === dependent.value);
    if (selected) trigger.value = selected.dataset[datasetKey];
    const filter = resetSelection => {
        const previousValue = dependent.value;
        allOptions.forEach(option => option.remove());
        for (const option of allOptions) {
            if (option.dataset[datasetKey] === trigger.value) dependent.appendChild(option);
        }
        dependent.disabled = !trigger.value;
        dependent.value = resetSelection ? '' : previousValue;
    };
    trigger.addEventListener('change', () => filter(true));
    filter(false);
}
```

- `trigger` / `dependent` are the two `<select>` elements (not ids — callers resolve their own elements, since `PurchaseOrders/Create.cshtml` has per-row selects reached via `row.querySelector`, not `getElementById`).
- `datasetKey` is the camelCase dataset property the dependent's `<option>`s carry (e.g. `'materialKey'` for `data-material-key`, `'rackId'` for `data-rack-id`) — this is what makes the one function cover both the material→thickness and rack→slot cases.
- The detach/re-append technique is the same fix described above: an element not in the DOM cannot render in the dropdown, unlike a merely-`hidden` one.

Each page loads the script (`<script src="~/js/dependent-select.js" asp-append-version="true"></script>`, matching the existing `app.css` link's convention) and calls `initDependentSelect(...)` once per dependent pair, replacing what used to be a ~15-line inline IIFE with a one-line call:

- Index.cshtml: `initDependentSelect(material, thickness, 'materialKey')` and `initDependentSelect(rack, slot, 'rackId')`
- Census/NewOpeningBalance.cshtml, Receiving/NewReceipt.cshtml: one `initDependentSelect(material, thickness, 'materialKey')` call each
- PurchaseOrders/Create.cshtml: one `initDependentSelect(...)` call per `.po-line` row, inside the existing `document.querySelectorAll('.po-line').forEach(row => ...)` loop; the unrelated "Add another line" button logic in the same script block is untouched

## Non-goals / no backend changes

- `IndexModel.MaterialLocations`, `LoadNewSheetOptionsAsync`, and `OnPostAddSheetAsync`'s validation (`MaterialLocations.All(x => x.InventoryLocationId != NewSheet.LocationId)`) are unchanged — they already operate on the slot's `InventoryLocationId`, which is exactly what the (still bound) slot select posts.
- Placeholder option text changes from "Choose confirmed rack slot" to "Choose rack" / "Choose slot" on the respective selects.
- No changes to `Receiving/SelectLocation.cshtml` or `Census/SelectSlot.cshtml` — those already present rack/slot as a table, not a dropdown, and are out of scope.
- No bundler/build step is introduced — `wwwroot/js/dependent-select.js` is a plain static file served as-is, same as `wwwroot/css/app.css`.
- The bound fields (`NewSheet.LocationId`, `ExistingMaterialSpecId` / `Lines[i].ExistingMaterialSpecId`), the C# models, and the server-side validation on each page are unchanged.

## Testing

Pure Razor/JS UI change; posted values are unchanged, so no new C# logic needs unit tests. Verify manually in the browser:
- Dialog opens with rack select populated and slot select disabled.
- Choosing a rack enables the slot select and filters it to that rack's slots only.
- Switching racks resets the slot selection.
- Submitting with a valid rack+slot still creates the sheet (existing `OnPostAddSheetAsync` flow).
- Submitting with no slot chosen still shows the existing "Choose an active material slot." error and redisplays the dialog with selections intact.
- On each of the four material/thickness dialogs (Index "Add a sheet", NewOpeningBalance, PurchaseOrders/Create — including adding a second PO line, NewReceipt): choosing a material shows only that material's thicknesses in the dropdown (not just disabled/grayed ones), switching materials updates the list, and submitting still saves correctly.
