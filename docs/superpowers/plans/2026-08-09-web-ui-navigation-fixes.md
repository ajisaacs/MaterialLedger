# Web UI Navigation-Gap Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the five navigation dead ends in the MaterialLedger web UI found by driving the app as a real user (Operator/Programmer) against the dev database: no reservation entry point, a 404 dead end on `Reservations/Create` without an id, silent empty states on three list pages, unlinked material-map item rows, and the "Exception queue" label reading like an error log.

**Architecture:** Presentation-only changes to existing Razor Pages (`.cshtml` and `.cshtml.cs`). No new pages, no data/service layer changes. Every "how do I get to X" gap is closed by linking to the existing `Inventory/Search` page (already the one page with full item filtering, including by `LocationCode`), not by building new pickers.

**Tech Stack:** ASP.NET Core 8 Razor Pages, Entity Framework Core, plain CSS (`wwwroot/css/app.css`, hand-written, no build step — the whole file is one minified line; edits must anchor on exact substrings).

## Global Constraints

- Spec: `docs/superpowers/specs/2026-08-09-web-ui-navigation-fixes-design.md` — read it if anything here is ambiguous.
- No changes to `MaterialLedger.Data` (service/data layer) or `MaterialLedger.Tests` — this is presentation-only.
- No new automated tests: the existing suite (`MaterialLedger.Tests`) covers the service/data layer only, and this plan doesn't add a page-testing framework. Every task is verified by building and by driving the running dev server with the Playwright browser tool.
- Follow existing CSS vocabulary in `app.css` (`--muted`, `--line`, `--white`, `--blue-dark`, `.content table`, `.form-error`) — new rules must look like part of the same system, not a new one.
- Dev server: `dotnet run --no-build --urls "http://localhost:59406"` from `MaterialLedger.Web/`, backed by the `forge` (10.10.100.134) dev database via `dotnet user-secrets` (already configured — `ConnectionStrings:MaterialLedger`, and `SeedAccounts:{0,1,2}:InitialPassword` = `dev-programmer-pw` / `dev-operator-pw` / `dev-manager-pw` for `programmer` / `operator` / `manager`).
- Rename scope is text-only: "Exception queue" → "Attention needed", "Exceptions" (Material Map column) → "Attention". Do **not** touch the `/Exceptions` route, the `Pages/Exceptions` folder/namespace, or `MaterialException`/`MaterialExceptionService`/`MaterialExceptions` — those are internal identifiers, not user-facing text.

---

### Task 1: Add `.empty-state` and `.info-banner` CSS rules

**Files:**
- Modify: `MaterialLedger.Web/wwwroot/css/app.css`

**Interfaces:**
- Produces: CSS classes `.empty-state` (muted, bordered placeholder block replacing a table when a list has no rows) and `.info-banner` (neutral-toned banner, same shape as the existing `.form-error` but blue/gray instead of red) — consumed by Tasks 3 and 5.

- [ ] **Step 1: Insert the two new rules into `app.css`**

The file is a single minified line. Find this exact substring (it's the `.form-error` rule immediately followed by the first `@media` block):

```css
.form-error{background:#fff0f0;border:1px solid #e9bbbb;color:#982c2c;font-size:.85rem;padding:.7rem .8rem}@media(max-width:800px)
```

Replace it with:

```css
.form-error{background:#fff0f0;border:1px solid #e9bbbb;color:#982c2c;font-size:.85rem;padding:.7rem .8rem}.info-banner{background:#eef5fa;border:1px solid #bfd7e6;color:var(--blue-dark);font-size:.85rem;padding:.7rem .8rem;margin:1.5rem 0}.empty-state{background:var(--white);border:1px solid var(--line);color:var(--muted);font-size:.85rem;margin:1.5rem 0;padding:1.5rem;text-align:center}@media(max-width:800px)
```

This inserts `.info-banner` and `.empty-state` right after `.form-error` and before the responsive breakpoint block, keeping the file's existing single-line convention.

- [ ] **Step 2: Build to confirm no syntax errors were introduced elsewhere**

Run: `dotnet build MaterialLedger.Web/MaterialLedger.Web.csproj -c Debug`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` (CSS isn't compiled, but this confirms the repo still builds after the edit — CSS mistakes are caught visually in Task 3/5's browser checks, not here).

- [ ] **Step 3: Commit**

```bash
git add MaterialLedger.Web/wwwroot/css/app.css
git commit -m "feat: add empty-state and info-banner CSS rules"
```

---

### Task 2: Rename "Exception queue" → "Attention needed"

**Files:**
- Modify: `MaterialLedger.Web/Pages/Shared/_Layout.cshtml:19`
- Modify: `MaterialLedger.Web/Pages/Index.cshtml:9`
- Modify: `MaterialLedger.Web/Pages/Exceptions/Index.cshtml:5`
- Modify: `MaterialLedger.Web/Pages/Locations/Index.cshtml:7`

**Interfaces:**
- None — pure text changes, no new members.

- [ ] **Step 1: Rename the sidebar nav link**

In `MaterialLedger.Web/Pages/Shared/_Layout.cshtml`, find:

```html
<a asp-page="/Reservations/Index">Reservations</a><a asp-page="/Exceptions/Index">Exception queue</a>
```

Replace with:

```html
<a asp-page="/Reservations/Index">Reservations</a><a asp-page="/Exceptions/Index">Attention needed</a>
```

- [ ] **Step 2: Rename the dashboard card**

In `MaterialLedger.Web/Pages/Index.cshtml`, find:

```html
    <a class="action-card" asp-page="/Exceptions/Index"><span>04</span><h3>Exception queue</h3><p>Review items that require attention.</p><b>Review exceptions →</b></a>
```

Replace with:

```html
    <a class="action-card" asp-page="/Exceptions/Index"><span>04</span><h3>Attention needed</h3><p>Review items that require attention.</p><b>Review →</b></a>
```

- [ ] **Step 3: Rename the page heading**

In `MaterialLedger.Web/Pages/Exceptions/Index.cshtml`, find:

```html
<h1>Exception queue</h1>
```

Replace with:

```html
<h1>Attention needed</h1>
```

- [ ] **Step 4: Rename the Material Map column header**

In `MaterialLedger.Web/Pages/Locations/Index.cshtml`, find:

```html
    <thead><tr><th>Code</th><th>Rack</th><th>Storage</th><th>Slots confirmed</th><th>Available</th><th>Reserved</th><th>Exceptions</th></tr></thead>
```

Replace with:

```html
    <thead><tr><th>Code</th><th>Rack</th><th>Storage</th><th>Slots confirmed</th><th>Available</th><th>Reserved</th><th>Attention</th></tr></thead>
```

- [ ] **Step 5: Build**

Run: `dotnet build MaterialLedger.Web/MaterialLedger.Web.csproj -c Debug`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 6: Verify in the browser**

With the dev server running at `http://localhost:59406` (start it if it isn't: `dotnet run --no-build --urls "http://localhost:59406"` from `MaterialLedger.Web/`, run in background) and logged in (`operator` / `dev-operator-pw` or `programmer` / `dev-programmer-pw`):

- Navigate to `http://localhost:59406/` — the sidebar nav shows "Attention needed" (not "Exception queue"), and the dashboard card reads "Attention needed" / "Review →".
- Navigate to `http://localhost:59406/Exceptions` — the page heading reads "Attention needed".
- Navigate to `http://localhost:59406/Locations` — the last table column header reads "Attention".

- [ ] **Step 7: Commit**

```bash
git add MaterialLedger.Web/Pages/Shared/_Layout.cshtml MaterialLedger.Web/Pages/Index.cshtml MaterialLedger.Web/Pages/Exceptions/Index.cshtml MaterialLedger.Web/Pages/Locations/Index.cshtml
git commit -m "feat: rename Exception queue to Attention needed in the UI"
```

---

### Task 3: Empty-state messages on Reservations, Closeout, and Exceptions

**Files:**
- Modify: `MaterialLedger.Web/Pages/Reservations/Index.cshtml`
- Modify: `MaterialLedger.Web/Pages/Closeout/Index.cshtml`
- Modify: `MaterialLedger.Web/Pages/Exceptions/Index.cshtml`

**Interfaces:**
- Consumes: `.empty-state` CSS class from Task 1.
- Consumes existing model properties: `IndexModel.Reservations` (`List<MaterialReservation>`) in both Reservations and Closeout `IndexModel`s; `IndexModel.OpenExceptions` / `IndexModel.ResolvedExceptions` (`List<MaterialException>`) in Exceptions `IndexModel`.

- [ ] **Step 1: Reservations empty state**

In `MaterialLedger.Web/Pages/Reservations/Index.cshtml`, find:

```html
<h1>Reservations</h1>
<table>
    <thead><tr><th>Item</th><th>Material</th><th>Job/Drawing</th><th>Equipment</th><th>Status</th><th>Created</th><th></th></tr></thead>
    <tbody>
    @foreach (var r in Model.Reservations)
    {
        <tr>
            <td>@r.InventoryItem?.PublicItemCode</td>
            <td>@r.InventoryItem?.MaterialSpec?.DisplayName</td>
            <td>@r.ShopOpsJobId @r.DrawingId @r.DrawingRevision</td>
            <td>@r.Equipment</td>
            <td>@r.Status</td>
            <td>@r.CreatedAtUtc.ToString("u")</td>
            <td><a asp-page="Details" asp-route-id="@r.MaterialReservationId">View</a></td>
        </tr>
    }
    </tbody>
</table>
```

Replace with:

```html
<h1>Reservations</h1>
@if (Model.Reservations.Count == 0)
{
    <p class="empty-state">No open reservations.</p>
}
else
{
<table>
    <thead><tr><th>Item</th><th>Material</th><th>Job/Drawing</th><th>Equipment</th><th>Status</th><th>Created</th><th></th></tr></thead>
    <tbody>
    @foreach (var r in Model.Reservations)
    {
        <tr>
            <td>@r.InventoryItem?.PublicItemCode</td>
            <td>@r.InventoryItem?.MaterialSpec?.DisplayName</td>
            <td>@r.ShopOpsJobId @r.DrawingId @r.DrawingRevision</td>
            <td>@r.Equipment</td>
            <td>@r.Status</td>
            <td>@r.CreatedAtUtc.ToString("u")</td>
            <td><a asp-page="Details" asp-route-id="@r.MaterialReservationId">View</a></td>
        </tr>
    }
    </tbody>
</table>
}
```

- [ ] **Step 2: Closeout empty state**

In `MaterialLedger.Web/Pages/Closeout/Index.cshtml`, find:

```html
<table>
    <thead><tr><th>Item</th><th>Material</th><th>Job/Drawing</th><th>Equipment</th><th>Status</th><th></th></tr></thead>
    <tbody>
    @foreach (var r in Model.Reservations)
    {
        <tr>
            <td>@r.InventoryItem?.PublicItemCode</td>
            <td>@r.InventoryItem?.MaterialSpec?.DisplayName</td>
            <td>@r.ShopOpsJobId @r.DrawingId @r.DrawingRevision</td>
            <td>@r.Equipment</td>
            <td>@r.Status</td>
            <td style="white-space:nowrap">
            @if (r.Status == ReservationStatus.Approved)
            {
                <form method="post" action="?handler=StartJob" style="display:inline">
                    @Html.AntiForgeryToken()
                    <input type="hidden" name="id" value="@r.MaterialReservationId" />
                    <input type="hidden" name="correlationId" value="@Model.CorrelationIdsByReservationId[r.MaterialReservationId]" />
                    <input type="hidden" name="expectedRowVersionBase64" value="@Convert.ToBase64String(r.RowVersion)" />
                    <button type="submit" style="font-size:1.1em; padding:0.6em 1em;">Start job</button>
                </form>
            }
            <a asp-page="Details" asp-route-id="@r.MaterialReservationId"><button type="button" style="font-size:1.1em; padding:0.6em 1em;">Close material</button></a>
            </td>
        </tr>
    }
    </tbody>
</table>
```

Replace with:

```html
@if (Model.Reservations.Count == 0)
{
    <p class="empty-state">Nothing awaiting closeout.</p>
}
else
{
<table>
    <thead><tr><th>Item</th><th>Material</th><th>Job/Drawing</th><th>Equipment</th><th>Status</th><th></th></tr></thead>
    <tbody>
    @foreach (var r in Model.Reservations)
    {
        <tr>
            <td>@r.InventoryItem?.PublicItemCode</td>
            <td>@r.InventoryItem?.MaterialSpec?.DisplayName</td>
            <td>@r.ShopOpsJobId @r.DrawingId @r.DrawingRevision</td>
            <td>@r.Equipment</td>
            <td>@r.Status</td>
            <td style="white-space:nowrap">
            @if (r.Status == ReservationStatus.Approved)
            {
                <form method="post" action="?handler=StartJob" style="display:inline">
                    @Html.AntiForgeryToken()
                    <input type="hidden" name="id" value="@r.MaterialReservationId" />
                    <input type="hidden" name="correlationId" value="@Model.CorrelationIdsByReservationId[r.MaterialReservationId]" />
                    <input type="hidden" name="expectedRowVersionBase64" value="@Convert.ToBase64String(r.RowVersion)" />
                    <button type="submit" style="font-size:1.1em; padding:0.6em 1em;">Start job</button>
                </form>
            }
            <a asp-page="Details" asp-route-id="@r.MaterialReservationId"><button type="button" style="font-size:1.1em; padding:0.6em 1em;">Close material</button></a>
            </td>
        </tr>
    }
    </tbody>
</table>
}
```

- [ ] **Step 3: Exceptions empty states (both sections)**

In `MaterialLedger.Web/Pages/Exceptions/Index.cshtml`, find:

```html
<h2>Open</h2>
<table>
    <thead><tr><th>Type</th><th>Item</th><th>Reservation</th><th>Opened</th><th>Notes</th><th></th></tr></thead>
    <tbody>
    @foreach (var e in Model.OpenExceptions)
    {
        <tr>
            <td>@e.ExceptionType</td>
            <td>@e.InventoryItem?.PublicItemCode @e.InventoryItem?.MaterialSpec?.DisplayName</td>
            <td>@(e.MaterialReservationId is not null ? $"#{e.MaterialReservationId}" : "-")</td>
            <td>@e.OpenedAtUtc.ToString("u") by @e.OpenedByUser?.DisplayName</td>
            <td>@e.Notes</td>
            <td>
            @if (Model.CanResolve)
            {
                <form method="post" action="?handler=Resolve" style="display:inline">
                    @Html.AntiForgeryToken()
                    <input type="hidden" name="id" value="@e.MaterialExceptionId" />
                    <input type="hidden" name="CorrelationId" value="@Model.CorrelationIdsByExceptionId[e.MaterialExceptionId]" />
                    <input type="text" name="ResolutionNotes" placeholder="Resolution notes" />
                    <button type="submit">Resolve</button>
                </form>
            }
            </td>
        </tr>
    }
    </tbody>
</table>

<h2>Recently resolved</h2>
<table>
    <thead><tr><th>Type</th><th>Item</th><th>Status</th><th>Resolved</th></tr></thead>
    <tbody>
    @foreach (var e in Model.ResolvedExceptions)
    {
        <tr>
            <td>@e.ExceptionType</td>
            <td>@e.InventoryItem?.PublicItemCode @e.InventoryItem?.MaterialSpec?.DisplayName</td>
            <td><strong>FIXED</strong></td>
            <td>@e.ResolvedAtUtc?.ToString("u") by @e.ResolvedByUser?.DisplayName</td>
        </tr>
    }
    </tbody>
</table>
```

Replace with:

```html
<h2>Open</h2>
@if (Model.OpenExceptions.Count == 0)
{
    <p class="empty-state">Nothing needs attention.</p>
}
else
{
<table>
    <thead><tr><th>Type</th><th>Item</th><th>Reservation</th><th>Opened</th><th>Notes</th><th></th></tr></thead>
    <tbody>
    @foreach (var e in Model.OpenExceptions)
    {
        <tr>
            <td>@e.ExceptionType</td>
            <td>@e.InventoryItem?.PublicItemCode @e.InventoryItem?.MaterialSpec?.DisplayName</td>
            <td>@(e.MaterialReservationId is not null ? $"#{e.MaterialReservationId}" : "-")</td>
            <td>@e.OpenedAtUtc.ToString("u") by @e.OpenedByUser?.DisplayName</td>
            <td>@e.Notes</td>
            <td>
            @if (Model.CanResolve)
            {
                <form method="post" action="?handler=Resolve" style="display:inline">
                    @Html.AntiForgeryToken()
                    <input type="hidden" name="id" value="@e.MaterialExceptionId" />
                    <input type="hidden" name="CorrelationId" value="@Model.CorrelationIdsByExceptionId[e.MaterialExceptionId]" />
                    <input type="text" name="ResolutionNotes" placeholder="Resolution notes" />
                    <button type="submit">Resolve</button>
                </form>
            }
            </td>
        </tr>
    }
    </tbody>
</table>
}

<h2>Recently resolved</h2>
@if (Model.ResolvedExceptions.Count == 0)
{
    <p class="empty-state">Nothing recently resolved.</p>
}
else
{
<table>
    <thead><tr><th>Type</th><th>Item</th><th>Status</th><th>Resolved</th></tr></thead>
    <tbody>
    @foreach (var e in Model.ResolvedExceptions)
    {
        <tr>
            <td>@e.ExceptionType</td>
            <td>@e.InventoryItem?.PublicItemCode @e.InventoryItem?.MaterialSpec?.DisplayName</td>
            <td><strong>FIXED</strong></td>
            <td>@e.ResolvedAtUtc?.ToString("u") by @e.ResolvedByUser?.DisplayName</td>
        </tr>
    }
    </tbody>
</table>
}
```

- [ ] **Step 4: Build**

Run: `dotnet build MaterialLedger.Web/MaterialLedger.Web.csproj -c Debug`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Verify in the browser**

Dev database currently has zero rows in all three lists, so this is directly visible without setup:

- Navigate to `http://localhost:59406/Reservations` — see "No open reservations." styled as a bordered, muted placeholder block (not a bare table header), no JS/console errors.
- Navigate to `http://localhost:59406/Closeout` — see "Nothing awaiting closeout."
- Navigate to `http://localhost:59406/Exceptions` — see "Nothing needs attention." under Open and "Nothing recently resolved." under Recently resolved.

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Web/Pages/Reservations/Index.cshtml MaterialLedger.Web/Pages/Closeout/Index.cshtml MaterialLedger.Web/Pages/Exceptions/Index.cshtml
git commit -m "feat: show empty-state messages on Reservations, Closeout, and Exceptions"
```

---

### Task 4: Link Material Map item rows to Inventory Search

**Files:**
- Modify: `MaterialLedger.Web/Pages/Locations/Details.cshtml`

**Interfaces:**
- Consumes: `Inventory/Search`'s existing `LocationCode` query-string filter
  (`SearchModel.LocationCode`, `MaterialLedger.Web/Pages/Inventory/Search.cshtml.cs:19`).

- [ ] **Step 1: Wrap each item's summary text in a link**

In `MaterialLedger.Web/Pages/Locations/Details.cshtml`, find:

```html
                <div style="@statusStyle">@item.PublicItemCode — @item.MaterialSpecDisplayName — @item.AvailableQuantity of @item.QuantityOnHand — @item.Status @(item.IsStale ? "(STALE)" : "")</div>
```

Replace with:

```html
                <div style="@statusStyle"><a asp-page="/Inventory/Search" asp-route-LocationCode="@slot.LocationCode">@item.PublicItemCode — @item.MaterialSpecDisplayName — @item.AvailableQuantity of @item.QuantityOnHand — @item.Status @(item.IsStale ? "(STALE)" : "")</a></div>
```

(`slot` is the enclosing `@foreach (var slot in Model.Slots)` loop variable already in scope at this point in the file — see the surrounding `<tr>` block.)

- [ ] **Step 2: Build**

Run: `dotnet build MaterialLedger.Web/MaterialLedger.Web.csproj -c Debug`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Verify in the browser**

- Navigate to `http://localhost:59406/Locations`, click "Laser 1 South Roll-Out Rack" (the only rack with an item as of this dev database — slot `L1-SOUTH-ROLL:S01` holds `SHEET-1`).
- On `http://localhost:59406/Locations/Details/L1-SOUTH-ROLL`, the `SHEET-1 — Carbon Steel A36...` text in slot `S01`'s Items column is now a link.
- Click it → lands on `http://localhost:59406/Inventory/Search?LocationCode=L1-SOUTH-ROLL%3AS01` (or similar, exact slot code) with the results table pre-filtered to that location and `SHEET-1` visible in it.

- [ ] **Step 4: Commit**

```bash
git add MaterialLedger.Web/Pages/Locations/Details.cshtml
git commit -m "feat: link Material Map item rows to filtered Inventory Search"
```

---

### Task 5: Reservation entry point — index link, Create redirect, Search banner

**Files:**
- Modify: `MaterialLedger.Web/Pages/Reservations/Index.cshtml`
- Modify: `MaterialLedger.Web/Pages/Reservations/Create.cshtml.cs:33-42`
- Modify: `MaterialLedger.Web/Pages/Inventory/Search.cshtml`

**Interfaces:**
- Consumes: `.info-banner` CSS class from Task 1.
- Produces: `TempData["Message"]` (`string`) — set by `Reservations/Create.cshtml.cs` `OnGetAsync`, read by `Inventory/Search.cshtml`. `TempData` is ASP.NET Core's built-in per-request-after-redirect store (cookie-backed here — `Program.cs` calls `AddRazorPages()`, which registers the default `CookieTempDataProvider`; no extra setup needed), available on every `PageModel`/`.cshtml` without new configuration.

- [ ] **Step 1: Add the "search inventory to reserve" link to Reservations index**

In `MaterialLedger.Web/Pages/Reservations/Index.cshtml`, find:

```html
<h1>Reservations</h1>
@if (Model.Reservations.Count == 0)
```

Replace with:

```html
<h1>Reservations</h1>
<p><a asp-page="/Inventory/Search">Search inventory to reserve material →</a></p>
@if (Model.Reservations.Count == 0)
```

(This lands after Task 3's edit, which is why the anchor includes the `@if` line — it confirms you're editing the post-Task-3 version of the file.)

- [ ] **Step 2: Make `Reservations/Create` redirect instead of 404 when the item is missing**

In `MaterialLedger.Web/Pages/Reservations/Create.cshtml.cs`, find:

```csharp
    public async Task<IActionResult> OnGetAsync(long inventoryItemId)
    {
        Item = await db.InventoryItems.Include(x => x.MaterialSpec).Include(x => x.InventoryLocation)
            .FirstOrDefaultAsync(x => x.InventoryItemId == inventoryItemId);
        if (Item is null)
            return NotFound();
        InventoryItemId = inventoryItemId;
        CorrelationId = Guid.NewGuid();
        return Page();
    }
```

Replace with:

```csharp
    public async Task<IActionResult> OnGetAsync(long inventoryItemId)
    {
        Item = await db.InventoryItems.Include(x => x.MaterialSpec).Include(x => x.InventoryLocation)
            .FirstOrDefaultAsync(x => x.InventoryItemId == inventoryItemId);
        if (Item is null)
        {
            TempData["Message"] = "Select an item from Inventory Search to create a reservation.";
            return RedirectToPage("/Inventory/Search");
        }
        InventoryItemId = inventoryItemId;
        CorrelationId = Guid.NewGuid();
        return Page();
    }
```

This covers both cases that reach here with no usable item: a route with no `inventoryItemId` segment at all (binds to `0`, no match) and a stale/bad id (deleted item, typo'd URL).

- [ ] **Step 3: Display the TempData message as an info banner on Inventory Search**

In `MaterialLedger.Web/Pages/Inventory/Search.cshtml`, find:

```html
@{ Layout = "Shared/_Layout"; }

<h1>Search inventory</h1>
```

Replace with:

```html
@{ Layout = "Shared/_Layout"; }

<h1>Search inventory</h1>
@if (TempData["Message"] is string message)
{
    <p class="info-banner">@message</p>
}
```

- [ ] **Step 4: Build**

Run: `dotnet build MaterialLedger.Web/MaterialLedger.Web.csproj -c Debug`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Verify the redirect and banner in the browser**

- Navigate to `http://localhost:59406/Reservations` — see the new "Search inventory to reserve material →" link under the heading (above or below the empty-state message depending on data at test time); click it → lands on `http://localhost:59406/Inventory/Search`.
- As the `programmer` user (`Reservations/Create` requires the `RequireProgrammerOrManager` policy — log out and log back in as `programmer` / `dev-programmer-pw` if currently on `operator`), navigate directly to `http://localhost:59406/Reservations/Create` (no id in the URL) — instead of a 404, you land on `http://localhost:59406/Inventory/Search` with the banner "Select an item from Inventory Search to create a reservation." visible at the top, styled as a blue/gray `.info-banner`, not the red `.form-error`.
- Regression check: from `Inventory/Search`, search with "Available only" checked, find `SHEET-1`, click its "Reserve" link → confirm `Reservations/Create` still renders normally (heading "Reserve SHEET-1", form fields, no banner) — the happy path with a valid id still works.

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Web/Pages/Reservations/Index.cshtml MaterialLedger.Web/Pages/Reservations/Create.cshtml.cs MaterialLedger.Web/Pages/Inventory/Search.cshtml
git commit -m "feat: add reservation entry point and redirect Create-without-item to Inventory Search"
```

---

## Final check

- [ ] Run the full existing test suite to confirm nothing outside the UI layer regressed: `dotnet test` from the repo root. Expected: all existing tests still pass (this plan touches no code they exercise, so this is a regression guard, not a feature test).
- [ ] Re-read `docs/superpowers/specs/2026-08-09-web-ui-navigation-fixes-design.md` end to end and confirm every numbered gap (1–5) has a corresponding completed task above.
