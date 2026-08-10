# Split rack/slot picker in Add-a-sheet dialog, and fix dependent-select filtering — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the single flat "Location" dropdown in the Index page's "Add a sheet" dialog into two dependent Rack/Slot selects, and fix the material→thickness dependent-select filter (used in four dialogs) so it actually removes non-matching options from the dropdown instead of unreliably toggling `hidden`/`disabled` — by extracting the filtering behavior into one shared static script instead of continuing to duplicate it per page.

**Architecture:** Pure Razor + vanilla-JS UI change, no backend/model changes. A new shared static asset, `wwwroot/js/dependent-select.js`, exposes one function, `initDependentSelect(trigger, dependent, datasetKey)`, implementing the corrected detach/re-append filtering technique. Each of the four dialogs (`Index.cshtml`, `Census/NewOpeningBalance.cshtml`, `PurchaseOrders/Create.cshtml`, `Receiving/NewReceipt.cshtml`) loads that script and replaces its inline ~15-line filter IIFE with a one-line call. `Index.cshtml` additionally splits its Location field into a Rack select and a Slot select, wired through the same shared function. This repo tests Razor page presentation by reading the `.cshtml`/`.cshtml.cs`/`.js` source as text and asserting on substrings (see `MaterialLedger.Tests/QuickSheetEntryPresentationTests.cs`, `ReceivingEntryPresentationTests.cs`) — there is no JS test runner or Playwright harness in the repo, so this plan follows that same source-assertion convention for the scripted tasks, plus a final manual/browser verification task.

**Tech Stack:** ASP.NET Core Razor Pages (.NET 8), vanilla JS (no framework/build step), xUnit for tests. Static files are already served (`Program.cs` calls `app.UseStaticFiles()`); `wwwroot/css/app.css` is the existing precedent for a shared static asset loaded via `~/`.

## Global Constraints

- No backend/C# model changes: `IndexModel.MaterialLocations`, `LoadNewSheetOptionsAsync`, `OnPostAddSheetAsync` validation, and the bound fields on all four pages (`NewSheet.LocationId`, `ExistingMaterialSpecId`, `Lines[i].ExistingMaterialSpecId`) stay exactly as they are today.
- Placeholder text: "Choose confirmed rack slot" → "Choose rack" (new rack select) / "Choose slot" (slot select).
- No changes to `Receiving/SelectLocation.cshtml` or `Census/SelectSlot.cshtml` (out of scope — those are tables, not dropdowns).
- The filtering fix is implemented exactly once, in `wwwroot/js/dependent-select.js`, and consumed identically by all five dependent-select pairs (the new rack/slot pair, plus the four existing material/thickness pairs). No page keeps its own copy of the filter logic — this duplication was explicitly reviewed and rejected in favor of the shared file (see spec's "Second problem" section).
- Each page loads the shared script the same way `Pages/Shared/_Layout.cshtml:7` loads `app.css`: `<script src="~/js/dependent-select.js" asp-append-version="true"></script>`.
- Spec: `docs/superpowers/specs/2026-08-10-split-rack-slot-picker-design.md`

---

### Task 1: Create the shared dependent-select filter script

**Files:**
- Create: `MaterialLedger.Web/wwwroot/js/dependent-select.js`
- Test: Create `MaterialLedger.Tests/DependentSelectScriptTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `initDependentSelect(trigger, dependent, datasetKey)` — a global JS function. `trigger`/`dependent` are `<select>` DOM elements (not ids — callers resolve their own elements). `datasetKey` is the camelCase dataset property name the dependent's filterable `<option>`s carry (e.g. `'materialKey'` for `data-material-key`, `'rackId'` for `data-rack-id`). Tasks 2-5 all call this function.

- [ ] **Step 1: Write the failing test**

Create `MaterialLedger.Tests/DependentSelectScriptTests.cs`:

```csharp
using Xunit;

namespace MaterialLedger.Tests;

public sealed class DependentSelectScriptTests
{
    [Fact]
    public void Shared_script_defines_a_reusable_dependent_select_filter()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "js", "dependent-select.js"));

        Assert.Contains("function initDependentSelect(trigger, dependent, datasetKey)", script);
        Assert.Contains("dependent.appendChild(option)", script);
        Assert.Contains("dependent.disabled = !trigger.value", script);
        Assert.DoesNotContain("option.hidden", script);
    }

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectScriptTests"`
Expected: FAIL (the file doesn't exist yet — `File.ReadAllText` throws `FileNotFoundException`).

- [ ] **Step 3: Create the shared script**

Create `MaterialLedger.Web/wwwroot/js/dependent-select.js`:

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

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectScriptTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/wwwroot/js/dependent-select.js MaterialLedger.Tests/DependentSelectScriptTests.cs
git commit -m "feat: add shared dependent-select filter script"
```

---

### Task 2: Split rack/slot picker and wire Index.cshtml to the shared script

**Files:**
- Modify: `MaterialLedger.Web/Pages/Index.cshtml:38-46` (Location field) and `:83-102` (script block)
- Test: `MaterialLedger.Tests/QuickSheetEntryPresentationTests.cs`

**Interfaces:**
- Consumes: `initDependentSelect(trigger, dependent, datasetKey)` from Task 1, loaded via `<script src="~/js/dependent-select.js" asp-append-version="true"></script>`.
- Consumes: `Model.MaterialLocations` (`List<InventoryLocation>`, unchanged — from `MaterialLedger.Web/Pages/Index.cshtml.cs:31,100`), `InventoryLocation.ParentInventoryLocation` (`InventoryLocation?`), `.InventoryLocationId` (`long`), `.LocationCode` (`string`), `.DisplayName` (`string`), `.SortOrder` (`int`).
- Produces: nothing consumed by later tasks — Tasks 3-5 touch different files.

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
    public void Add_sheet_dialog_wires_both_dependent_selects_through_the_shared_script()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Index.cshtml"));

        Assert.Contains("<script src=\"~/js/dependent-select.js\" asp-append-version=\"true\"></script>", page);
        Assert.Contains("initDependentSelect(document.getElementById('new-sheet-material'), document.getElementById('new-sheet-thickness'), 'materialKey')", page);
        Assert.Contains("initDependentSelect(document.getElementById('new-sheet-rack'), document.getElementById('new-sheet-slot'), 'rackId')", page);
        Assert.DoesNotContain("option.hidden", page);
        Assert.DoesNotContain("filterThicknesses", page);
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

- [ ] **Step 4: Replace the script block to use the shared filter**

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
    <script src="~/js/dependent-select.js" asp-append-version="true"></script>
    <script>
        initDependentSelect(document.getElementById('new-sheet-material'), document.getElementById('new-sheet-thickness'), 'materialKey');
        initDependentSelect(document.getElementById('new-sheet-rack'), document.getElementById('new-sheet-slot'), 'rackId');
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
git commit -m "feat: split rack/slot picker in Add-a-sheet dialog and wire it to the shared filter script"
```

---

### Task 3: Wire Census/NewOpeningBalance.cshtml to the shared script

**Files:**
- Modify: `MaterialLedger.Web/Pages/Census/NewOpeningBalance.cshtml:80-99`
- Test: Create `MaterialLedger.Tests/DependentSelectPagePresentationTests.cs`

**Interfaces:**
- Consumes: `initDependentSelect(trigger, dependent, datasetKey)` from Task 1.
- Produces: `DependentSelectPagePresentationTests` class and its `ReadPage(params string[])` / `RepositoryRoot` helpers — Tasks 4 and 5 add more `[Fact]` methods to this same file.

- [ ] **Step 1: Write the failing test**

Create `MaterialLedger.Tests/DependentSelectPagePresentationTests.cs`:

```csharp
using Xunit;

namespace MaterialLedger.Tests;

public sealed class DependentSelectPagePresentationTests
{
    [Fact]
    public void Opening_balance_page_wires_its_dependent_select_through_the_shared_script()
    {
        var page = ReadPage("Census", "NewOpeningBalance.cshtml");

        Assert.Contains("<script src=\"~/js/dependent-select.js\" asp-append-version=\"true\"></script>", page);
        Assert.Contains("initDependentSelect(document.getElementById('opening-balance-material'), document.getElementById('opening-balance-thickness'), 'materialKey')", page);
        Assert.DoesNotContain("option.hidden", page);
        Assert.DoesNotContain("filterThicknesses", page);
    }

    private static string ReadPage(params string[] pathSegments) => File.ReadAllText(
        Path.Combine(new[] { RepositoryRoot, "MaterialLedger.Web", "Pages" }.Concat(pathSegments).ToArray()));

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectPagePresentationTests"`
Expected: FAIL (old inline script still in place).

- [ ] **Step 3: Replace the script block**

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
<script src="~/js/dependent-select.js" asp-append-version="true"></script>
<script>
    initDependentSelect(document.getElementById('opening-balance-material'), document.getElementById('opening-balance-thickness'), 'materialKey');
</script>
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectPagePresentationTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/Census/NewOpeningBalance.cshtml MaterialLedger.Tests/DependentSelectPagePresentationTests.cs
git commit -m "fix: hide filtered-out thicknesses on the opening balance page"
```

---

### Task 4: Wire PurchaseOrders/Create.cshtml to the shared script

**Files:**
- Modify: `MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml:65-90`
- Test: `MaterialLedger.Tests/DependentSelectPagePresentationTests.cs` (add a method)

**Interfaces:**
- Consumes: `initDependentSelect(trigger, dependent, datasetKey)` from Task 1; `DependentSelectPagePresentationTests`'s `ReadPage` helper from Task 3.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write the failing test**

Add to `MaterialLedger.Tests/DependentSelectPagePresentationTests.cs` (inside the existing class, alongside the Task 3 method):

```csharp
    [Fact]
    public void Purchase_order_lines_wire_their_dependent_selects_through_the_shared_script()
    {
        var page = ReadPage("PurchaseOrders", "Create.cshtml");

        Assert.Contains("<script src=\"~/js/dependent-select.js\" asp-append-version=\"true\"></script>", page);
        Assert.Contains("initDependentSelect(row.querySelector('.po-line-material'), row.querySelector('.po-line-thickness'), 'materialKey')", page);
        Assert.DoesNotContain("option.hidden", page);
        Assert.DoesNotContain("filterThicknesses", page);
        Assert.Contains("document.getElementById('add-line').addEventListener('click'", page);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectPagePresentationTests"`
Expected: the new test FAILs; the Task 3 test still PASSes.

- [ ] **Step 3: Replace the script block**

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
<script src="~/js/dependent-select.js" asp-append-version="true"></script>
<script>
    document.querySelectorAll('.po-line').forEach(row => {
        initDependentSelect(row.querySelector('.po-line-material'), row.querySelector('.po-line-thickness'), 'materialKey');
    });

    const lines = Array.from(document.querySelectorAll('.po-line'));
    document.getElementById('add-line').addEventListener('click', () => {
        const next = lines.find(row => row.style.display === 'none');
        if (next) next.style.display = '';
    });
</script>
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectPagePresentationTests"`
Expected: both tests in the file PASS.

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml MaterialLedger.Tests/DependentSelectPagePresentationTests.cs
git commit -m "fix: hide filtered-out thicknesses on purchase order lines"
```

---

### Task 5: Wire Receiving/NewReceipt.cshtml to the shared script

**Files:**
- Modify: `MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml:79-98`
- Test: `MaterialLedger.Tests/DependentSelectPagePresentationTests.cs` (add a method)

**Interfaces:**
- Consumes: `initDependentSelect(trigger, dependent, datasetKey)` from Task 1; `DependentSelectPagePresentationTests`'s `ReadPage` helper from Task 3.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write the failing test**

Add to `MaterialLedger.Tests/DependentSelectPagePresentationTests.cs`:

```csharp
    [Fact]
    public void Receiving_page_wires_its_dependent_select_through_the_shared_script()
    {
        var page = ReadPage("Receiving", "NewReceipt.cshtml");

        Assert.Contains("<script src=\"~/js/dependent-select.js\" asp-append-version=\"true\"></script>", page);
        Assert.Contains("initDependentSelect(document.getElementById('receipt-material'), document.getElementById('receipt-thickness'), 'materialKey')", page);
        Assert.DoesNotContain("option.hidden", page);
        Assert.DoesNotContain("filterThicknesses", page);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectPagePresentationTests"`
Expected: the new test FAILs; the Task 3 and Task 4 tests still PASS.

- [ ] **Step 3: Replace the script block**

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
<script src="~/js/dependent-select.js" asp-append-version="true"></script>
<script>
    initDependentSelect(document.getElementById('receipt-material'), document.getElementById('receipt-thickness'), 'materialKey');
</script>
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test MaterialLedger.Tests --filter "FullyQualifiedName~DependentSelectPagePresentationTests"`
Expected: all three tests in the file PASS.

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/Receiving/NewReceipt.cshtml MaterialLedger.Tests/DependentSelectPagePresentationTests.cs
git commit -m "fix: hide filtered-out thicknesses when receiving material"
```

---

### Task 6: Full test suite check and browser verification

**Files:** none (verification only).

**Interfaces:**
- Consumes: the completed changes from Tasks 1-5.
- Produces: nothing (terminal task).

- [ ] **Step 1: Run the full automated test suite**

Run: `dotnet test`
Expected: all tests pass, including the pre-existing `QuickSheetEntryPresentationTests`, and the new `DependentSelectScriptTests` and `DependentSelectPagePresentationTests`. (A pre-existing, unrelated test — `MaterialTransactionPermissionsTests.MaterialledgerApp_CannotUpdateMaterialTransactions` — fails without a live-database connection string configured via `MATERIALLEDGER_TEST_APP_CONNECTION`; that failure is expected and out of scope.)

- [ ] **Step 2: Start the dev server and verify in a real browser**

Per project convention (see the `run` skill and `webapp-testing` skill), launch `MaterialLedger.Web` (e.g. `dotnet run --project MaterialLedger.Web`) and sign in as a user with the Programmer or Manager role (required for the "Add a sheet" dialog and the purchase-order/receiving pages). Walk through each of these and confirm against the spec's testing checklist (`docs/superpowers/specs/2026-08-10-split-rack-slot-picker-design.md`):

- **Home page → "Add a sheet" dialog:** opens with the Rack select populated and the Slot select disabled; choosing a rack enables the Slot select and shows only that rack's slots; switching racks clears the slot selection; submitting a valid rack+slot creates the sheet; submitting with no slot chosen still shows "Choose an active material slot." and redisplays the dialog with the rack/slot selections intact.
- **Home page → "Add a sheet" dialog, Material/Thickness fields:** choosing a material shows only that material's thicknesses in the dropdown (not grayed-out entries for other materials); switching materials updates the list.
- **`/Census/NewOpeningBalance`:** same material/thickness check as above.
- **`/PurchaseOrders/Create`:** same check on the first PO line, then click "Add another line" and confirm the second line's material/thickness selects filter independently of the first.
- **`/Receiving/NewReceipt`:** same material/thickness check as above.
- **Browser console:** confirm no JavaScript errors on any of the four pages (e.g. `dependent-select.js` failing to load would show as a 404 and a `ReferenceError: initDependentSelect is not defined`).

- [ ] **Step 3: Report results**

If any check fails, fix the underlying task's code before proceeding — do not move on with a known-broken check. Once every item above is confirmed working, this plan is complete; no commit is needed for this verification-only task.
