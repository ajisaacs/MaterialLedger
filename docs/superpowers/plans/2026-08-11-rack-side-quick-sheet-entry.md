# Rack-Side Quick Sheet Entry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Control board's "Add a sheet" dialog stay open across multiple adds — resetting slot/material/thickness/size/notes/quarantine but keeping the chosen rack — so an operator standing at one rack can record a whole stack of sheets without navigating away or re-picking the rack each time.

**Architecture:** `IndexModel.OnPostAddSheetAsync` (`MaterialLedger.Web/Pages/Index.cshtml.cs`) changes from "validate → redirect to `/Census/Success`" to "validate → return `JsonResult`". A new vanilla-JS file (`wwwroot/js/quick-sheet-entry.js`, matching the existing `dependent-select.js` pattern) intercepts the dialog form's submit, `fetch()`s the handler, and on success appends a row to an in-dialog running list and resets everything except the rack selection. `OpeningBalanceService` (the actual business logic) is unchanged.

**Tech Stack:** ASP.NET Core Razor Pages (.NET), Entity Framework Core, vanilla JS (no framework), xUnit tests against a real SQL Server test database (`SqlServerTestDatabaseFixture`).

## Global Constraints

- No changes to `Receiving/*`, `Census/SelectSlot`, `Census/NewOpeningBalance`, `Census/Success`, or `OpeningBalanceService` — this is a Control-board-dialog-only change.
- The dialog still always records exactly one full sheet per add (`InventoryItemKind.FullSheet`, quantity `1`) — no quantity field.
- No inline undo/delete for a row already added in the session — mistakes are fixed later via the existing inventory item edit page.
- The server generates the idempotency correlation ID fresh on every `OnPostAddSheetAsync` call (`Guid.NewGuid()` inside the handler) — do not reintroduce a client-supplied/bound `CorrelationId`, and do not use `crypto.randomUUID()` client-side (requires a secure context the shop-floor LAN deployment can't guarantee).
- Integration tests that use `SqlServerTestDatabaseFixture` require `MATERIALLEDGER_TEST_ADMIN_CONNECTION` to be set to an sa-level connection string for `forge-sql` (10.10.100.134) — they are deliberately not silently skipped if it's missing.

---

### Task 1: JSON contract for `OnPostAddSheetAsync`

**Files:**
- Modify: `MaterialLedger.Web/Pages/Index.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Index.cshtml:32-38` (error paragraph + hidden `CorrelationId` input) and `:98-101` (`ShowAddSheetDialog` auto-open script)
- Modify: `MaterialLedger.Tests/QuickSheetEntryPresentationTests.cs`
- Create: `MaterialLedger.Tests/QuickSheetEntryIntegrationTests.cs`

**Interfaces:**
- Produces (consumed by Task 2's JS): a `JsonResult` from `?handler=AddSheet`, serialized camelCase (ASP.NET Core's default), shaped as:
  - Success: `{ success: true, error: null, itemCode: string, slotCode: string, materialDisplayName: string, usableWidthIn: number|null, usableLengthIn: number|null }`
  - Failure: `{ success: false, error: string, itemCode: null, slotCode: null, materialDisplayName: null, usableWidthIn: null, usableLengthIn: null }`
- Produces (consumed by Task 2's markup edits): the dialog no longer contains `<input type="hidden" asp-for="NewSheet.CorrelationId" />`, a Razor-conditional error paragraph, or the `ShowAddSheetDialog` auto-open script — Task 2 must not assume any of these still exist.
- Produces (markup elements Task 2's JS binds to by id): `#add-sheet-log` (container, starts `hidden`), `#add-sheet-log-list` (`<ul>` inside it), `#add-sheet-error` (`<p>`, starts `hidden`).

- [ ] **Step 1: Write the failing integration test**

Create `MaterialLedger.Tests/QuickSheetEntryIntegrationTests.cs`:

```csharp
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Census;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Pages;
using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class QuickSheetEntryIntegrationTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public QuickSheetEntryIntegrationTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long specId, long rackId, long slotId)> SeedAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
        var guid = Guid.NewGuid().ToString("N");
        var spec = new MaterialSpec { MaterialFamily = $"Plate-{guid}"[..30], Grade = "A36", ThicknessIn = 0.1875m, DisplayName = "A36 3/16in", IsActive = true };
        db.AddRange(user, spec);
        await db.SaveChangesAsync();

        var rack = new InventoryLocation
        {
            LocationCode = $"RACK-{guid}"[..30], DisplayName = "East drop rack", LocationType = InventoryLocationType.Rack,
            StorageMode = InventoryStorageMode.VerticalDropBay, IsActive = true, CapacityConfirmed = true
        };
        db.InventoryLocations.Add(rack);
        await db.SaveChangesAsync();

        var slot = new InventoryLocation
        {
            LocationCode = $"SLOT-{guid}"[..30], DisplayName = "East drop rack — A1", LocationType = InventoryLocationType.Shelf,
            StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true, CapacityConfirmed = true,
            ParentInventoryLocationId = rack.InventoryLocationId
        };
        db.InventoryLocations.Add(slot);
        await db.SaveChangesAsync();

        return (user.AppUserId, spec.MaterialSpecId, rack.InventoryLocationId, slot.InventoryLocationId);
    }

    private static IndexModel NewModel(MaterialLedgerDbContext db, int userId) => new(
        new WorkQueueQueryService(db, new InventoryQueryService(db, freshnessThresholdDays: 90)),
        db,
        new OpeningBalanceService(db, new OperationLogGuard(db)))
    {
        PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, "Manager")], "test"))
            }
        }
    };

    [Fact]
    public async Task OnPostAddSheetAsync_GoldenPath_ReturnsSuccessJsonAndCreatesItem()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId, _, slotId) = await SeedAsync(db);

        var model = NewModel(db, userId);
        model.NewSheet = new NewSheetInput { LocationId = slotId, ExistingMaterialSpecId = specId, UsableWidthIn = 48m, UsableLengthIn = 96m };

        var result = await model.OnPostAddSheetAsync();

        var json = Assert.IsType<JsonResult>(result);
        var response = Assert.IsType<AddSheetResponse>(json.Value);
        Assert.True(response.Success);
        Assert.Null(response.Error);
        Assert.NotNull(response.ItemCode);
        Assert.StartsWith("SHEET-", response.ItemCode);
        Assert.Equal("A36 3/16in", response.MaterialDisplayName);
        Assert.Equal(48m, response.UsableWidthIn);
        Assert.Equal(96m, response.UsableLengthIn);

        Assert.Equal(1, await db.InventoryItems.CountAsync(x => x.PublicItemCode == response.ItemCode));
    }

    [Fact]
    public async Task OnPostAddSheetAsync_InvalidSlot_ReturnsErrorJsonAndCreatesNothing()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId, _, _) = await SeedAsync(db);

        var model = NewModel(db, userId);
        model.NewSheet = new NewSheetInput { LocationId = -1, ExistingMaterialSpecId = specId };

        var result = await model.OnPostAddSheetAsync();

        var json = Assert.IsType<JsonResult>(result);
        var response = Assert.IsType<AddSheetResponse>(json.Value);
        Assert.False(response.Success);
        Assert.Equal("Choose an active material slot.", response.Error);
        Assert.Equal(0, await db.InventoryItems.CountAsync());
    }

    [Fact]
    public async Task OnPostAddSheetAsync_TwoConsecutiveAddsInSameSession_BothSucceed()
    {
        await using var db = _fixture.CreateContext();
        var (userId, specId, _, slotId) = await SeedAsync(db);

        var model = NewModel(db, userId);
        model.NewSheet = new NewSheetInput { LocationId = slotId, ExistingMaterialSpecId = specId, UsableWidthIn = 48m, UsableLengthIn = 96m };
        var first = Assert.IsType<AddSheetResponse>(Assert.IsType<JsonResult>(await model.OnPostAddSheetAsync()).Value);

        model.NewSheet = new NewSheetInput { LocationId = slotId, ExistingMaterialSpecId = specId, UsableWidthIn = 30m, UsableLengthIn = 60m };
        var second = Assert.IsType<AddSheetResponse>(Assert.IsType<JsonResult>(await model.OnPostAddSheetAsync()).Value);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.NotEqual(first.ItemCode, second.ItemCode);
        Assert.Equal(2, await db.InventoryItems.CountAsync());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MaterialLedger.Tests --filter FullyQualifiedName~QuickSheetEntryIntegrationTests`
Expected: build error — `AddSheetResponse` does not exist yet, and `NewSheetInput` has no matching constructor signature without `CorrelationId`. (Set `MATERIALLEDGER_TEST_ADMIN_CONNECTION` first if it isn't already set in your shell, per the Global Constraints note — the build error will show either way.)

- [ ] **Step 3: Rewrite `IndexModel` to return JSON**

Replace the contents of `MaterialLedger.Web/Pages/Index.cshtml.cs` with:

```csharp
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Census;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Pages.Shared;
using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages;

[Authorize]
public class IndexModel(
    WorkQueueQueryService workQueue,
    MaterialLedgerDbContext db,
    OpeningBalanceService openingBalance) : PageModel
{
    public WorkQueueSummary Summary { get; private set; } = new()
    {
        AvailableMaterialCount = 0,
        ApprovalNeededCount = 0,
        ReadyForCuttingCount = 0,
        CutResultNeededCount = 0,
        AttentionNeededCount = 0,
        RecentWork = []
    };

    [BindProperty] public NewSheetInput NewSheet { get; set; } = new();
    public List<InventoryLocation> MaterialLocations { get; private set; } = [];
    public List<MaterialSpec> AvailableSpecs { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Summary = await workQueue.GetAsync();
        await LoadNewSheetOptionsAsync();
    }

    public async Task<IActionResult> OnPostAddSheetAsync()
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager"))
            return Forbid();

        await LoadNewSheetOptionsAsync();

        var slot = MaterialLocations.FirstOrDefault(x => x.InventoryLocationId == NewSheet.LocationId);
        if (slot is null)
            return new JsonResult(new AddSheetResponse(false, "Choose an active material slot."));

        if (NewSheet.ExistingMaterialSpecId is null)
            return new JsonResult(new AddSheetResponse(false, "Choose a material and thickness from the catalog."));

        var spec = AvailableSpecs.FirstOrDefault(x => x.MaterialSpecId == NewSheet.ExistingMaterialSpecId);
        if (spec is null)
            return new JsonResult(new AddSheetResponse(false, "The selected material is no longer available. Please choose another."));

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var result = await openingBalance.RecordOpeningBalanceAsync(
                Guid.NewGuid(),
                new OpeningBalanceRequest(
                    NewSheet.LocationId,
                    NewSheet.ExistingMaterialSpecId.Value,
                    InventoryItemKind.FullSheet,
                    1,
                    NewSheet.UsableWidthIn,
                    NewSheet.UsableLengthIn,
                    ShapeClassification.Rectangular,
                    NewSheet.IsQuarantined,
                    NewSheet.Notes),
                userId);

            return new JsonResult(new AddSheetResponse(
                true, null, result.PublicItemCode, slot.LocationCode, spec.DisplayName,
                NewSheet.UsableWidthIn, NewSheet.UsableLengthIn));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or MaterialLedger.Data.Idempotency.OperationConflictException)
        {
            var message = ex is MaterialLedger.Data.Idempotency.OperationConflictException
                ? "This form was already submitted with different values — please review and resubmit."
                : ex.Message;
            return new JsonResult(new AddSheetResponse(false, message));
        }
    }

    private async Task LoadNewSheetOptionsAsync()
    {
        MaterialLocations = await ActiveMaterialSlotQuery.Create(db).ToListAsync();
        AvailableSpecs = await db.MaterialSpecs
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.MaterialFamily).ThenBy(x => x.Grade).ThenBy(x => x.Finish).ThenBy(x => x.ThicknessIn)
            .ToListAsync();
    }
}

public class NewSheetInput
{
    public long LocationId { get; set; }
    public long? ExistingMaterialSpecId { get; set; }
    public decimal? UsableWidthIn { get; set; }
    public decimal? UsableLengthIn { get; set; }
    public bool IsQuarantined { get; set; }
    public string? Notes { get; set; }
}

public record AddSheetResponse(
    bool Success,
    string? Error,
    string? ItemCode = null,
    string? SlotCode = null,
    string? MaterialDisplayName = null,
    decimal? UsableWidthIn = null,
    decimal? UsableLengthIn = null);
```

This drops `CorrelationId` from `NewSheetInput`, drops `NewSheetError`/`ShowAddSheetDialog` from `IndexModel`, and changes `OnPostAddSheetAsync` to always return a `JsonResult`.

- [ ] **Step 4: Update `Index.cshtml` so the project builds again**

In `MaterialLedger.Web/Pages/Index.cshtml`, replace:

```html
        @if (Model.NewSheetError is not null)
        {
            <p class="form-error" role="alert">@Model.NewSheetError</p>
        }
        <form method="post" action="?handler=AddSheet" class="sheet-entry-form">
            @Html.AntiForgeryToken()
            <input type="hidden" asp-for="NewSheet.CorrelationId" />
            <label>Rack
```

with:

```html
        <div id="add-sheet-log" class="sheet-entry-log" hidden>
            <p class="form-hint">Added this session:</p>
            <ul id="add-sheet-log-list"></ul>
        </div>
        <p id="add-sheet-error" class="form-error" role="alert" hidden></p>
        <form method="post" action="?handler=AddSheet" class="sheet-entry-form">
            @Html.AntiForgeryToken()
            <label>Rack
```

Then delete this block entirely (it sat right after the `initDependentSelect(...)` `<script>` block, just before the closing `}` of the `@if (User.IsInRole("Programmer") || User.IsInRole("Manager"))` section):

```html
    @if (Model.ShowAddSheetDialog)
    {
        <script>document.addEventListener('DOMContentLoaded', () => document.getElementById('add-sheet-dialog').showModal());</script>
    }
```

- [ ] **Step 5: Update the existing presentation test for the removed/added markup**

In `MaterialLedger.Tests/QuickSheetEntryPresentationTests.cs`, add a new test (keep all existing tests as-is — they don't reference `CorrelationId`, `NewSheetError`, or `ShowAddSheetDialog`, so they still pass unchanged):

```csharp
    [Fact]
    public void Add_sheet_dialog_has_no_client_bound_correlation_id_and_provides_a_session_log_and_error_slot()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Index.cshtml"));

        Assert.DoesNotContain("NewSheet.CorrelationId", page);
        Assert.DoesNotContain("Model.ShowAddSheetDialog", page);
        Assert.Contains("id=\"add-sheet-log\"", page);
        Assert.Contains("id=\"add-sheet-log-list\"", page);
        Assert.Contains("id=\"add-sheet-error\"", page);
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test MaterialLedger.Tests --filter FullyQualifiedName~QuickSheetEntryIntegrationTests|FullyQualifiedName~QuickSheetEntryPresentationTests`
Expected: PASS (all `QuickSheetEntryIntegrationTests` and `QuickSheetEntryPresentationTests` tests green). Also run `dotnet build` to confirm the whole solution still compiles (the dialog form still native-submits at this point since Task 2 hasn't added the JS interceptor yet — that's expected and fixed next task).

- [ ] **Step 7: Commit**

```bash
git add MaterialLedger.Web/Pages/Index.cshtml.cs MaterialLedger.Web/Pages/Index.cshtml MaterialLedger.Tests/QuickSheetEntryPresentationTests.cs MaterialLedger.Tests/QuickSheetEntryIntegrationTests.cs
git commit -m "$(cat <<'EOF'
Return JSON from Add-a-sheet handler instead of redirecting

Lays the backend groundwork for keeping the dialog open across
multiple adds: the handler now reports success/failure as JSON
with a server-generated correlation id per call, instead of
redirecting to a separate success page after every single sheet.
EOF
)"
```

---

### Task 2: Client-side fetch wiring, running list, and reset behavior

**Files:**
- Create: `MaterialLedger.Web/wwwroot/js/quick-sheet-entry.js`
- Modify: `MaterialLedger.Web/Pages/Index.cshtml` (script include + init call only — the `<script>` block right after `initDependentSelect(...)` calls, before the closing `}`)
- Modify: `MaterialLedger.Web/wwwroot/css/app.css`
- Create: `MaterialLedger.Tests/QuickSheetEntryScriptTests.cs`

**Interfaces:**
- Consumes (from Task 1): `POST ?handler=AddSheet` returns JSON `{ success, error, itemCode, slotCode, materialDisplayName, usableWidthIn, usableLengthIn }`; markup ids `#add-sheet-log`, `#add-sheet-log-list`, `#add-sheet-error`, `#add-sheet-dialog`, `#new-sheet-rack`, `#new-sheet-slot`, `#new-sheet-material`, `#new-sheet-thickness`.
- Consumes (from existing `dependent-select.js`): `initDependentSelect(trigger, dependent, datasetKey)` is already wired for both pairs; this task does not modify that file.
- Produces: global function `initAddSheetForm(dialog)`, called once per page load with the `<dialog>` element.

- [ ] **Step 1: Write the failing script presentation test**

Create `MaterialLedger.Tests/QuickSheetEntryScriptTests.cs`:

```csharp
using Xunit;

namespace MaterialLedger.Tests;

public sealed class QuickSheetEntryScriptTests
{
    [Fact]
    public void Script_intercepts_submit_and_fetches_instead_of_navigating()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "js", "quick-sheet-entry.js"));

        Assert.Contains("function initAddSheetForm(dialog)", script);
        Assert.Contains("event.preventDefault()", script);
        Assert.Contains("fetch(form.action", script);
        Assert.Contains("new FormData(form)", script);
    }

    [Fact]
    public void Script_resets_slot_and_material_but_leaves_rack_selection_on_success()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "js", "quick-sheet-entry.js"));

        Assert.Contains("slotSelect.value = ''", script);
        Assert.Contains("materialSelect.value = ''", script);
        Assert.DoesNotContain("rackSelect.value = ''", script);
        Assert.Contains("slotSelect.focus()", script);
    }

    [Fact]
    public void Script_appends_a_running_list_row_on_success_and_clears_it_when_the_dialog_closes()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "js", "quick-sheet-entry.js"));

        Assert.Contains("logList.appendChild(row)", script);
        Assert.Contains("dialog.addEventListener('close'", script);
        Assert.Contains("logList.replaceChildren()", script);
    }

    [Fact]
    public void Index_page_loads_the_script_and_wires_it_to_the_dialog()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Index.cshtml"));

        Assert.Contains("<script src=\"~/js/quick-sheet-entry.js\" asp-append-version=\"true\"></script>", page);
        Assert.Contains("initAddSheetForm(document.getElementById('add-sheet-dialog'))", page);
    }

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MaterialLedger.Tests --filter FullyQualifiedName~QuickSheetEntryScriptTests`
Expected: FAIL — `quick-sheet-entry.js` doesn't exist yet, and `Index.cshtml` doesn't reference it yet.

- [ ] **Step 3: Create `quick-sheet-entry.js`**

Create `MaterialLedger.Web/wwwroot/js/quick-sheet-entry.js`:

```js
function initAddSheetForm(dialog) {
    const form = dialog.querySelector('.sheet-entry-form');
    const rackSelect = document.getElementById('new-sheet-rack');
    const slotSelect = document.getElementById('new-sheet-slot');
    const materialSelect = document.getElementById('new-sheet-material');
    const widthInput = form.querySelector('[name="NewSheet.UsableWidthIn"]');
    const lengthInput = form.querySelector('[name="NewSheet.UsableLengthIn"]');
    const quarantineInput = form.querySelector('[name="NewSheet.IsQuarantined"]');
    const notesInput = form.querySelector('[name="NewSheet.Notes"]');
    const submitButton = form.querySelector('button[type="submit"]');
    const errorEl = document.getElementById('add-sheet-error');
    const logWrap = document.getElementById('add-sheet-log');
    const logList = document.getElementById('add-sheet-log-list');

    const clearItemFields = () => {
        materialSelect.value = '';
        materialSelect.dispatchEvent(new Event('change'));
        slotSelect.value = '';
        widthInput.value = '';
        lengthInput.value = '';
        quarantineInput.checked = false;
        notesInput.value = '';
    };

    form.addEventListener('submit', async event => {
        event.preventDefault();
        errorEl.hidden = true;
        submitButton.disabled = true;
        submitButton.textContent = 'Adding…';
        try {
            const response = await fetch(form.action, { method: 'post', body: new FormData(form) });
            const result = await response.json();
            if (result.success) {
                const row = document.createElement('li');
                const size = result.usableWidthIn && result.usableLengthIn
                    ? ` — ${result.usableWidthIn} × ${result.usableLengthIn}`
                    : '';
                row.textContent = `${result.itemCode} — Slot ${result.slotCode} — ${result.materialDisplayName}${size}`;
                logList.appendChild(row);
                logWrap.hidden = false;
                clearItemFields();
                slotSelect.focus();
            } else {
                errorEl.textContent = result.error;
                errorEl.hidden = false;
            }
        } catch {
            errorEl.textContent = 'Could not reach the server — check your connection and try again.';
            errorEl.hidden = false;
        } finally {
            submitButton.disabled = false;
            submitButton.textContent = 'Add sheet';
        }
    });

    dialog.addEventListener('close', () => {
        logList.replaceChildren();
        logWrap.hidden = true;
    });
}
```

- [ ] **Step 4: Wire the script into `Index.cshtml`**

In `MaterialLedger.Web/Pages/Index.cshtml`, find:

```html
    <script src="~/js/dependent-select.js" asp-append-version="true"></script>
    <script>
        initDependentSelect(document.getElementById('new-sheet-material'), document.getElementById('new-sheet-thickness'), 'materialKey');
        initDependentSelect(document.getElementById('new-sheet-rack'), document.getElementById('new-sheet-slot'), 'rackId');
    </script>
```

and add immediately after it (still inside the same `@if (User.IsInRole("Programmer") || User.IsInRole("Manager"))` block):

```html
    <script src="~/js/quick-sheet-entry.js" asp-append-version="true"></script>
    <script>
        initAddSheetForm(document.getElementById('add-sheet-dialog'));
    </script>
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test MaterialLedger.Tests --filter FullyQualifiedName~QuickSheetEntryScriptTests`
Expected: PASS

- [ ] **Step 6: Add running-list styling**

In `MaterialLedger.Web/wwwroot/css/app.css`, add near the other `.sheet-entry-*` rules (after `.dialog-actions button { margin: 0; }`):

```css
.sheet-entry-log {
  background: var(--surface);
  border: 1px solid var(--line);
  margin-bottom: 0.9rem;
  max-height: 8rem;
  overflow-y: auto;
  padding: 0.7rem 0.9rem;
}
.sheet-entry-log p {
  margin: 0 0 0.4rem;
}
.sheet-entry-log ul {
  list-style: none;
  margin: 0;
  padding: 0;
}
.sheet-entry-log li {
  border-top: 1px solid var(--line);
  padding: 0.3rem 0;
}
.sheet-entry-log li:first-child {
  border-top: none;
}
```

(`--surface` and `--line` are both defined in `:root` at the top of `app.css` and already used throughout the file.)

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test MaterialLedger.Tests`
Expected: PASS (all tests, including the ones from Task 1)

- [ ] **Step 8: Manually verify in the browser**

Run: `dotnet run --project MaterialLedger.Web`

1. Log in as a Programmer or Manager user.
2. On the Control board, click **Add a sheet**.
3. Choose a rack, then a slot, a material/thickness, and a size. Click **Add sheet**.
4. Confirm: the dialog stays open, the button briefly reads "Adding…", a row appears in a running list at the top of the dialog, and the rack/slot/material/thickness/size fields are all cleared except the rack — which still shows your chosen rack.
5. Immediately pick a different slot on the same rack and add a second sheet. Confirm the running list now shows two rows and the rack didn't change.
6. Try submitting with no slot chosen (or pick a slot, then trigger an error some other way, e.g. by editing an inactive material into the request via devtools if needed) — confirm the error message appears inline and nothing in the form is cleared.
7. Close the dialog (× button), reopen it, and confirm the running list is empty again.

- [ ] **Step 9: Commit**

```bash
git add MaterialLedger.Web/wwwroot/js/quick-sheet-entry.js MaterialLedger.Web/Pages/Index.cshtml MaterialLedger.Web/wwwroot/css/app.css MaterialLedger.Tests/QuickSheetEntryScriptTests.cs
git commit -m "$(cat <<'EOF'
Keep Add-a-sheet dialog open across adds, reset per item

Recording several sheets at one rack no longer requires navigating
away and reopening the dialog for every item: submitting now fetches
in place, appends to a running list of what's been added this
session, and clears slot/material/thickness/size while leaving the
rack selection alone.
EOF
)"
```
