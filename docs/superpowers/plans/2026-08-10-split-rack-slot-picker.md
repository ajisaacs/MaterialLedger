# Split rack/slot picker in Add-a-sheet dialog, and fix dependent-select filtering — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the single flat "Location" dropdown in the Index page's "Add a sheet" dialog into two dependent Rack/Slot selects, and fix the material→thickness dependent-select filter (used in four dialogs) so it actually removes non-matching options from the dropdown instead of unreliably toggling `hidden`/`disabled`.

**Architecture:** Pure Razor + vanilla-JS UI change, no backend/model changes. Each affected page keeps its existing bound field and server-side validation; only the `<select>` markup and its `<script>` block change. The rack/slot select is new (built with the same detach/re-append filtering technique that the thickness fix introduces), and the thickness filter in all four dialogs is rewritten to use that same technique. This repo tests Razor page presentation by reading the `.cshtml`/`.cshtml.cs` source as text and asserting on substrings (see `MaterialLedger.Tests/QuickSheetEntryPresentationTests.cs`, `ReceivingEntryPresentationTests.cs`) — there is no JS test runner or Playwright harness in the repo, so this plan follows that same source-assertion convention for the scripted tasks, plus a final manual/browser verification task.

**Tech Stack:** ASP.NET Core Razor Pages (.NET 8), vanilla JS (no framework/build step), xUnit for tests.

## Global Constraints

- No backend/C# model changes: `IndexModel.MaterialLocations`, `LoadNewSheetOptionsAsync`, `OnPostAddSheetAsync` validation, and the bound fields on all four pages (`NewSheet.LocationId`, `ExistingMaterialSpecId`, `Lines[i].ExistingMaterialSpecId`) stay exactly as they are today.
- Placeholder text: "Choose confirmed rack slot" → "Choose rack" (new rack select) / "Choose slot" (slot select).
- No changes to `Receiving/SelectLocation.cshtml` or `Census/SelectSlot.cshtml` (out of scope — those are tables, not dropdowns).
- The corrected filter technique (detach/re-append `<option>` elements, not `hidden`/`disabled` toggling) is used in all five places it's needed: the new rack/slot select, and the four existing material/thickness selects (Index "Add a sheet", `Census/NewOpeningBalance.cshtml`, `PurchaseOrders/Create.cshtml`, `Receiving/NewReceipt.cshtml`).
- Spec: `docs/superpowers/specs/2026-08-10-split-rack-slot-picker-design.md`

---

### Task 1: Split rack/slot picker and fix thickness filter in Index.cshtml ("Add a sheet" dialog)

**Files:**
- Modify: `MaterialLedger.Web/Pages/Index.cshtml:38-46` (Location field) and `:83-102` (script block)
- Test: `MaterialLedger.Tests/QuickSheetEntryPresentationTests.cs`

**Interfaces:**
- Consumes: `Model.MaterialLocations` (`List<InventoryLocation>`, unchanged — from `MaterialLedger.Web/Pages/Index.cshtml.cs:31,100`), `InventoryLocation.ParentInventoryLocation` (`InventoryLocation?`), `.InventoryLocationId` (`long`), `.LocationCode` (`string`), `.DisplayName` (`string`), `.SortOrder` (`int`).
- Produces: nothing consumed by later tasks — this task is self-contained (Tasks 2-4 touch different files).

- [ ] **Step 1: Write the failing tests**

Add two new `[Fact]` methods to `MaterialLedger.Tests/QuickSheetEntryPresentationTests.cs` (keep the existing `Control_board_offers_a_modal_for_recording_one_sheet_at_a_confirmed_location` and `Control_board_model_loads_only_active_material_slots_for_the_new_sheet_location_dropdown` methods and the `RepositoryRoot` helper as-is):

```csharp
    [Fact]
    public void Add_sheet_dialog_splits_location_into_a_rack_select_and_a_dependent_slot_select()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Index.cshtml"));

        Assert.Contains("id=\"new-sheet-rack\"", page);
        Assert.Contains("Choose rack", page);
        Assert.Contains("id=\"new-sheet-slot\"", page);
        Assert.Contains("Choose slot", page);
        Assert.Contains("data-rack-id=\"@location.ParentInventoryLocationId\"", page);
        Assert.Contains("asp-for=\"NewSheet.LocationId\" id=\"new-sheet-slot\"", page);
        Assert.DoesNotContain("Choose confirmed rack slot", page);
        Assert.DoesNotContain("@location.ParentInventoryLocation?.DisplayName — @location.LocationCode", page);
    }

    [Fact]
    public void Add_sheet_dialog_filters_dependent_selects_by_detaching_options_instead_of_hiding_them()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Index.cshtml"));

        Assert.DoesNotContain("option.hidden", page);
        Assert.Contains("allThicknessOptions", page);
        Assert.Contains("thickness.appendChild(option)", page);
        Assert.Contains("allSlotOptions", page);
        Assert.Contains("slot.appendChild(option)", page);
        Assert.Contains("filterSlots", page);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~QuickSheetEntryPresentationTests"`
Expected: the two new tests FAIL (old markup/script still in place); the two pre-existing tests in this file still PASS.

- [ ] **Step 3: Replace the Location field with Rack + Slot selects**

In `MaterialLedger.Web/Pages/Index.cshtml`, replace (around line 38-46):

```html
            <label>Location
                <select asp-for="NewSheet.LocationId" required>
                    <option value="">Choose confirmed rack slot</option>
                    @foreach (var location in Model.MaterialLocations)
                    {
                        <option value="@location.InventoryLocationId">@location.ParentInventoryLocation?.DisplayName — @location.LocationCode</option>
                    }
                </select>
            </label>
```

with:

```html
            <label>Rack
                <select id="new-sheet-rack">
                    <option value="">Choose rack</option>
                    @foreach (var rack in Model.MaterialLocations.Select(x => x.ParentInventoryLocation).Where(x => x is not null).DistinctBy(x => x!.InventoryLocationId).OrderBy(x => x!.SortOrder))
                    {
                        <option value="@rack!.InventoryLocationId">@rack.DisplayName</option>
                    }
                </select>
            </label>
            <label>Slot
                <select asp-for="NewSheet.LocationId" id="new-sheet-slot" required disabled>
                    <option value="">Choose slot</option>
                    @foreach (var location in Model.MaterialLocations)
                    {
                        <option value="@location.InventoryLocationId" data-rack-id="@location.ParentInventoryLocationId">@location.LocationCode</option>
                    }
                </select>
            </label>
```

- [ ] **Step 4: Rewrite the script block to fix thickness filtering and add slot filtering**

In the same file, replace the `<script>` block (around line 83-102):

```html
    <script>
        (() => {
            const material = document.getElementById('new-sheet-material');
            const thickness = document.getElementById('new-sheet-thickness');
            const selected = thickness.options[thickness.selectedIndex];
            if (selected?.dataset.materialKey) material.value = selected.dataset.materialKey;
            const filterThicknesses = (resetSelection) => {
                for (const option of thickness.options) {
                    if (!option.value) continue;
                    const matches = option.dataset.materialKey === material.value;
                    option.hidden = !matches;
                    option.disabled = !matches;
                }
                thickness.disabled = !material.value;
                if (resetSelection) thickness.value = '';
            };
            material.addEventListener('change', () => filterThicknesses(true));
            filterThicknesses(false);
        })();
    </script>
```

with:

```html
    <script>
        (() => {
            const material = document.getElementById('new-sheet-material');
            const thickness = document.getElementById('new-sheet-thickness');
            const allThicknessOptions = Array.from(thickness.querySelectorAll('option[data-material-key]'));
            const selectedThickness = allThicknessOptions.find(option => option.value === thickness.value);
            if (selectedThickness?.dataset.materialKey) material.value = selectedThickness.dataset.materialKey;
            const filterThicknesses = resetSelection => {
                const previousValue = thickness.value;
                thickness.querySelectorAll('option[data-material-key]').forEach(option => option.remove());
                for (const option of allThicknessOptions) {
                    if (option.dataset.materialKey === material.value) thickness.appendChild(option);
                }
                thickness.disabled = !material.value;
                thickness.value = resetSelection ? '' : previousValue;
            };
            material.addEventListener('change', () => filterThicknesses(true));
            filterThicknesses(false);
        })();
        (() => {
            const rack = document.getElementById('new-sheet-rack');
            const slot = document.getElementById('new-sheet-slot');
            const allSlotOptions = Array.from(slot.querySelectorAll('option[data-rack-id]'));
            const selectedSlot = allSlotOptions.find(option => option.value === slot.value);
            if (selectedSlot?.dataset.rackId) rack.value = selectedSlot.dataset.rackId;
            const filterSlots = resetSelection => {
                const previousValue = slot.value;
                slot.querySelectorAll('option[data-rack-id]').forEach(option => option.remove());
                for (const option of allSlotOptions) {
                    if (option.dataset.rackId === rack.value) slot.appendChild(option);
                }
                slot.disabled = !rack.value;
                slot.value = resetSelection ? '' : previousValue;
            };
            rack.addEventListener('change', () => filterSlots(true));
            filterSlots(false);
        })();
    </script>
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~QuickSheetEntryPresentationTests"`
Expected: all four tests in the file PASS.

- [ ] **Step 6: Build the web project to catch any Razor compile errors**

Run: `dotnet build MaterialLedger.Web`
Expected: build succeeds (0 errors). `DistinctBy` requires no extra `@using` — `ImplicitUsings` is enabled project-wide and this file already uses other `System.Linq` methods (`GroupBy`) without one.

- [ ] **Step 7: Commit**

```bash
git add MaterialLedger.Web/Pages/Index.cshtml MaterialLedger.Tests/QuickSheetEntryPresentationTests.cs
git commit -m "feat: split rack/slot picker and fix thickness filter in Add-a-sheet dialog"
```

---

### Task 2: Fix thickness filter technique in Census/NewOpeningBalance.cshtml

**Files:**
- Modify: `MaterialLedger.Web/Pages/Census/NewOpeningBalance.cshtml:80-99`
- Test: Create `MaterialLedger.Tests/DependentSelectFilteringPresentationTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `DependentSelectFilteringPresentationTests` class and its `ReadPage(params string[])` / `RepositoryRoot` helpers — Tasks 3 and 4 add more `[Fact]` methods to this same file.

- [ ] **Step 1: Write the failing test**

Create `MaterialLedger.Tests/DependentSelectFilteringPresentationTests.cs`:

```csharp
using Xunit;

namespace MaterialLedger.Tests;

public sealed class DependentSelectFilteringPresentationTests
{
    [Fact]
    public void Opening_balance_page_filters_thicknesses_by_detaching_options_instead_of_hiding_them()
    {
        var page = ReadPage("Census", "NewOpeningBalance.cshtml");

        Assert.DoesNotContain("option.hidden", page);
        Assert.Contains("allThicknessOptions", page);
        Assert.Contains("thickness.appendChild(option)", page);
    }

    private static string ReadPage(params string[] pathSegments) => File.ReadAllText(
        Path.Combine(new[] { RepositoryRoot, "MaterialLedger.Web", "Pages" }.Concat(pathSegments).ToArray()));

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectFilteringPresentationTests"`
Expected: FAIL (old `option.hidden` script still in place).

- [ ] **Step 3: Rewrite the script block**

In `MaterialLedger.Web/Pages/Census/NewOpeningBalance.cshtml`, replace the `<script>` block (around line 80-99):

```html
<script>
    (() => {
        const material = document.getElementById('opening-balance-material');
        const thickness = document.getElementById('opening-balance-thickness');
        const selected = thickness.options[thickness.selectedIndex];
        if (selected?.dataset.materialKey) material.value = selected.dataset.materialKey;
        const filterThicknesses = reset => {
            for (const option of thickness.options) {
                if (!option.value) continue;
                const matches = option.dataset.materialKey === material.value;
                option.hidden = !matches;
                option.disabled = !matches;
            }
            thickness.disabled = !material.value;
            if (reset) thickness.value = '';
        };
        material.addEventListener('change', () => filterThicknesses(true));
        filterThicknesses(false);
    })();
</script>
```

with:

```html
<script>
    (() => {
        const material = document.getElementById('opening-balance-material');
        const thickness = document.getElementById('opening-balance-thickness');
        const allThicknessOptions = Array.from(thickness.querySelectorAll('option[data-material-key]'));
        const selectedThickness = allThicknessOptions.find(option => option.value === thickness.value);
        if (selectedThickness?.dataset.materialKey) material.value = selectedThickness.dataset.materialKey;
        const filterThicknesses = reset => {
            const previousValue = thickness.value;
            thickness.querySelectorAll('option[data-material-key]').forEach(option => option.remove());
            for (const option of allThicknessOptions) {
                if (option.dataset.materialKey === material.value) thickness.appendChild(option);
            }
            thickness.disabled = !material.value;
            thickness.value = reset ? '' : previousValue;
        };
        material.addEventListener('change', () => filterThicknesses(true));
        filterThicknesses(false);
    })();
</script>
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectFilteringPresentationTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/Census/NewOpeningBalance.cshtml MaterialLedger.Tests/DependentSelectFilteringPresentationTests.cs
git commit -m "fix: hide filtered-out thicknesses on the opening balance page"
```

---

### Task 3: Fix thickness filter technique in PurchaseOrders/Create.cshtml

**Files:**
- Modify: `MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml:65-90`
- Test: `MaterialLedger.Tests/DependentSelectFilteringPresentationTests.cs` (add a method)

**Interfaces:**
- Consumes: `DependentSelectFilteringPresentationTests`'s `ReadPage` helper from Task 2.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write the failing test**

Add to `MaterialLedger.Tests/DependentSelectFilteringPresentationTests.cs` (inside the existing class, alongside the Task 2 method):

```csharp
    [Fact]
    public void Purchase_order_lines_filter_thicknesses_by_detaching_options_instead_of_hiding_them()
    {
        var page = ReadPage("PurchaseOrders", "Create.cshtml");

        Assert.DoesNotContain("option.hidden", page);
        Assert.Contains("allThicknessOptions", page);
        Assert.Contains("thickness.appendChild(option)", page);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectFilteringPresentationTests"`
Expected: the new test FAILs; the Task 2 test still PASSes.

- [ ] **Step 3: Rewrite the script block**

In `MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml`, replace the `<script>` block (around line 65-90):

```html
<script>
    document.querySelectorAll('.po-line').forEach(row => {
        const material = row.querySelector('.po-line-material');
        const thickness = row.querySelector('.po-line-thickness');
        const selected = thickness.options[thickness.selectedIndex];
        if (selected?.dataset.materialKey) material.value = selected.dataset.materialKey;
        const filterThicknesses = reset => {
            for (const option of thickness.options) {
                if (!option.value) continue;
                const matches = option.dataset.materialKey === material.value;
                option.hidden = !matches;
                option.disabled = !matches;
            }
            thickness.disabled = !material.value;
            if (reset) thickness.value = '';
        };
        material.addEventListener('change', () => filterThicknesses(true));
        filterThicknesses(false);
    });

    const lines = Array.from(document.querySelectorAll('.po-line'));
    document.getElementById('add-line').addEventListener('click', () => {
        const next = lines.find(row => row.style.display === 'none');
        if (next) next.style.display = '';
    });
</script>
```

with:

```html
<script>
    document.querySelectorAll('.po-line').forEach(row => {
        const material = row.querySelector('.po-line-material');
        const thickness = row.querySelector('.po-line-thickness');
        const allThicknessOptions = Array.from(thickness.querySelectorAll('option[data-material-key]'));
        const selectedThickness = allThicknessOptions.find(option => option.value === thickness.value);
        if (selectedThickness?.dataset.materialKey) material.value = selectedThickness.dataset.materialKey;
        const filterThicknesses = reset => {
            const previousValue = thickness.value;
            thickness.querySelectorAll('option[data-material-key]').forEach(option => option.remove());
            for (const option of allThicknessOptions) {
                if (option.dataset.materialKey === material.value) thickness.appendChild(option);
            }
            thickness.disabled = !material.value;
            thickness.value = reset ? '' : previousValue;
        };
        material.addEventListener('change', () => filterThicknesses(true));
        filterThicknesses(false);
    });

    const lines = Array.from(document.querySelectorAll('.po-line'));
    document.getElementById('add-line').addEventListener('click', () => {
        const next = lines.find(row => row.style.display === 'none');
        if (next) next.style.display = '';
    });
</script>
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectFilteringPresentationTests"`
Expected: both tests in the file PASS.

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml MaterialLedger.Tests/DependentSelectFilteringPresentationTests.cs
git commit -m "fix: hide filtered-out thicknesses on purchase order lines"
```

---

### Task 4: Fix thickness filter technique in Receiving/NewReceipt.cshtml

**Files:**
- Modify: `MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml:79-98`
- Test: `MaterialLedger.Tests/DependentSelectFilteringPresentationTests.cs` (add a method)

**Interfaces:**
- Consumes: `DependentSelectFilteringPresentationTests`'s `ReadPage` helper from Task 2.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write the failing test**

Add to `MaterialLedger.Tests/DependentSelectFilteringPresentationTests.cs`:

```csharp
    [Fact]
    public void Receiving_page_filters_thicknesses_by_detaching_options_instead_of_hiding_them()
    {
        var page = ReadPage("Receiving", "NewReceipt.cshtml");

        Assert.DoesNotContain("option.hidden", page);
        Assert.Contains("allThicknessOptions", page);
        Assert.Contains("thickness.appendChild(option)", page);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectFilteringPresentationTests"`
Expected: the new test FAILs; the Task 2 and Task 3 tests still PASS.

- [ ] **Step 3: Rewrite the script block**

In `MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml`, replace the `<script>` block (around line 79-98):

```html
<script>
    (() => {
        const material = document.getElementById('receipt-material');
        const thickness = document.getElementById('receipt-thickness');
        const selected = thickness.options[thickness.selectedIndex];
        if (selected?.dataset.materialKey) material.value = selected.dataset.materialKey;
        const filterThicknesses = reset => {
            for (const option of thickness.options) {
                if (!option.value) continue;
                const matches = option.dataset.materialKey === material.value;
                option.hidden = !matches;
                option.disabled = !matches;
            }
            thickness.disabled = !material.value;
            if (reset) thickness.value = '';
        };
        material.addEventListener('change', () => filterThicknesses(true));
        filterThicknesses(false);
    })();
</script>
```

with:

```html
<script>
    (() => {
        const material = document.getElementById('receipt-material');
        const thickness = document.getElementById('receipt-thickness');
        const allThicknessOptions = Array.from(thickness.querySelectorAll('option[data-material-key]'));
        const selectedThickness = allThicknessOptions.find(option => option.value === thickness.value);
        if (selectedThickness?.dataset.materialKey) material.value = selectedThickness.dataset.materialKey;
        const filterThicknesses = reset => {
            const previousValue = thickness.value;
            thickness.querySelectorAll('option[data-material-key]').forEach(option => option.remove());
            for (const option of allThicknessOptions) {
                if (option.dataset.materialKey === material.value) thickness.appendChild(option);
            }
            thickness.disabled = !material.value;
            thickness.value = reset ? '' : previousValue;
        };
        material.addEventListener('change', () => filterThicknesses(true));
        filterThicknesses(false);
    })();
</script>
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectFilteringPresentationTests"`
Expected: all three tests in the file PASS.

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml MaterialLedger.Tests/DependentSelectFilteringPresentationTests.cs
git commit -m "fix: hide filtered-out thicknesses when receiving material"
```

---

### Task 5: Full test suite check and browser verification

**Files:** none (verification only).

**Interfaces:**
- Consumes: the completed changes from Tasks 1-4.
- Produces: nothing (terminal task).

- [ ] **Step 1: Run the full automated test suite**

Run: `dotnet test`
Expected: all tests pass, including the pre-existing `QuickSheetEntryPresentationTests` and the new `DependentSelectFilteringPresentationTests`.

- [ ] **Step 2: Start the dev server and verify in a real browser**

Per project convention (see the `run` skill and `webapp-testing` skill), launch `MaterialLedger.Web` (e.g. `dotnet run --project MaterialLedger.Web`) and sign in as a user with the Programmer or Manager role (required for the "Add a sheet" dialog and the purchase-order/receiving pages). Walk through each of these and confirm against the spec's testing checklist (`docs/superpowers/specs/2026-08-10-split-rack-slot-picker-design.md`):

- **Home page → "Add a sheet" dialog:** opens with the Rack select populated and the Slot select disabled; choosing a rack enables the Slot select and shows only that rack's slots; switching racks clears the slot selection; submitting a valid rack+slot creates the sheet; submitting with no slot chosen still shows "Choose an active material slot." and redisplays the dialog with the rack/slot selections intact.
- **Home page → "Add a sheet" dialog, Material/Thickness fields:** choosing a material shows only that material's thicknesses in the dropdown (not grayed-out entries for other materials); switching materials updates the list.
- **`/Census/NewOpeningBalance`:** same material/thickness check as above.
- **`/PurchaseOrders/Create`:** same check on the first PO line, then click "Add another line" and confirm the second line's material/thickness selects filter independently of the first.
- **`/Receiving/NewReceipt`:** same material/thickness check as above.

- [ ] **Step 3: Report results**

If any check fails, fix the underlying task's code before proceeding — do not move on with a known-broken check. Once every item above is confirmed working, this plan is complete; no commit is needed for this verification-only task.
