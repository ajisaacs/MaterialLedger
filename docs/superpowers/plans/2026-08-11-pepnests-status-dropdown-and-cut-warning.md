# PEP Nests Status Dropdown and Cut Warning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the free-text status filter on `/PepNests` with a real dropdown defaulting to "To be cut," and add a prominent warning banner on the nest Details page when a program has already been cut.

**Architecture:** Two independent, additive changes to existing Razor Pages in `MaterialLedger.Web/Pages/PepNests/` — no new files, no service/API contract changes. Both are verified with plain text-assertion tests against the raw `.cshtml`/`.cs` source, matching the existing style in `PepNestPresentationTests.cs`.

**Tech Stack:** ASP.NET Core Razor Pages (.NET 8), xUnit.

## Global Constraints

- The 12 status strings must be copied verbatim from `PepApi.Core/PepApi.Core/PepHelper.cs:84-101` (`PepHelper.GetStatus`) — these are the exact strings the PEP API sends over the wire, and `NestFilterData.Apply` on the PEP side matches `Status` exactly (case-insensitively), so any typo silently breaks filtering for that status.
- No changes to `PepApi.Core` — it is a separate deployed service; this plan only touches `MaterialLedger.Web`.
- No changes to `PepNestQueryService`'s public signatures — `GetFilterOptionsAsync` keeps returning both `Statuses` and `Customers`; `IndexModel` simply stops reading `.Statuses` off it.

---

## Task 1: Status filter becomes a dropdown, defaulting to "To be cut"

**Files:**
- Modify: `MaterialLedger.Web/Pages/PepNests/Index.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/PepNests/Index.cshtml`
- Test: `MaterialLedger.Tests/PepNestPresentationTests.cs`

**Interfaces:**
- Produces: `IndexModel.StatusOptions` — `public static readonly IReadOnlyList<string> StatusOptions`, the fixed 12-value list, referenced from `Index.cshtml`.
- Produces: `IndexModel.Status` — `[BindProperty(SupportsGet = true)] public string? Status { get; set; } = "To be cut";` (was `null` default).

- [ ] **Step 1: Write the failing tests**

Add to `MaterialLedger.Tests/PepNestPresentationTests.cs` (inside the existing `PepNestPresentationTests` class, after `PEP_nest_browser_shows_newest_first_and_a_descriptive_material_with_grade`):

```csharp
    [Fact]
    public void PEP_nest_browser_status_filter_is_a_dropdown_defaulting_to_To_be_cut()
    {
        var page = ReadPage("PepNests", "Index.cshtml");
        var model = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "PepNests", "Index.cshtml.cs"));

        Assert.Contains("<select asp-for=\"Status\">", page);
        Assert.DoesNotContain("list=\"statusOptions\"", page);
        Assert.Contains("All statuses", page);
        Assert.Contains("\"To be cut\"", page);
        Assert.Contains("\"Has been cut\"", page);

        Assert.Contains("Status { get; set; } = \"To be cut\";", model);
        Assert.Contains("\"To be cut\", \"Quote\", \"Has been cut\", \"Hold\",", model);
        Assert.Contains("\"Quote, accepted, to be cut\", \"Quote, accepted, has been cut\",", model);
        Assert.Contains("\"Quote, accepted, hold\", \"Quote, accepted, moved to wip\",", model);
        Assert.Contains("\"Quote, rejected\", \"Quote, timed out\", \"Deleted\", \"N/A\",", model);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MaterialLedger.Tests --filter PEP_nest_browser_status_filter_is_a_dropdown_defaulting_to_To_be_cut`
Expected: FAIL (the `<select>`, `"All statuses"`, and the new `Status` default don't exist yet).

- [ ] **Step 3: Update `IndexModel`**

Replace the full contents of `MaterialLedger.Web/Pages/PepNests/Index.cshtml.cs` with:

```csharp
using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.PepNests;

[Authorize]
public class IndexModel(PepNestQueryService pepNests) : PageModel
{
    public static readonly IReadOnlyList<string> StatusOptions =
    [
        "To be cut", "Quote", "Has been cut", "Hold",
        "Quote, accepted, to be cut", "Quote, accepted, has been cut",
        "Quote, accepted, hold", "Quote, accepted, moved to wip",
        "Quote, rejected", "Quote, timed out", "Deleted", "N/A",
    ];

    [BindProperty(SupportsGet = true)] public int Year { get; set; } = DateTime.UtcNow.Year;
    [BindProperty(SupportsGet = true)] public string? Status { get; set; } = "To be cut";
    [BindProperty(SupportsGet = true)] public string? Customer { get; set; }
    public IReadOnlyList<PepNestSummary> Nests { get; private set; } = [];
    public IReadOnlyList<string> CustomerOptions { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task OnGetAsync()
    {
        if (Year is < 2000 or > 2100) { Error = "Enter a four-digit nest year."; return; }
        try { Nests = (await pepNests.GetNestsAsync(Year, Status, Customer)).OrderByDescending(x => x.DateCreated).ToList(); }
        catch (HttpRequestException) { Error = "PEP nests could not be reached. Try again after the PEP API is available."; }

        try { CustomerOptions = (await pepNests.GetFilterOptionsAsync(Year)).Customers; }
        catch (HttpRequestException) { }
    }
}
```

This removes the instance `StatusOptions` property (previously populated from
`GetFilterOptionsAsync(Year).Statuses`) and replaces it with the static fixed
list. `Status` now defaults to `"To be cut"`.

- [ ] **Step 4: Update `Index.cshtml`**

Replace the `<form class="filter-bar">` block in `MaterialLedger.Web/Pages/PepNests/Index.cshtml` (currently lines 11-22):

```html
<form method="get" class="filter-bar">
    <label>Year <input asp-for="Year" type="number" min="2000" max="2100" /></label>
    <label>Status <input asp-for="Status" list="statusOptions" placeholder="To be cut" autocomplete="off" /></label>
    <label>Customer <input asp-for="Customer" list="customerOptions" autocomplete="off" /></label>
    <datalist id="statusOptions">
        @foreach (var status in Model.StatusOptions) { <option value="@status"></option> }
    </datalist>
    <datalist id="customerOptions">
        @foreach (var customer in Model.CustomerOptions) { <option value="@customer"></option> }
    </datalist>
    <button type="submit">Find nests</button>
</form>
```

with:

```html
<form method="get" class="filter-bar">
    <label>Year <input asp-for="Year" type="number" min="2000" max="2100" /></label>
    <label>Status
        <select asp-for="Status">
            <option value="">All statuses</option>
            @foreach (var status in IndexModel.StatusOptions) { <option value="@status">@status</option> }
        </select>
    </label>
    <label>Customer <input asp-for="Customer" list="customerOptions" autocomplete="off" /></label>
    <datalist id="customerOptions">
        @foreach (var customer in Model.CustomerOptions) { <option value="@customer"></option> }
    </datalist>
    <button type="submit">Find nests</button>
</form>
```

Note `IndexModel.StatusOptions` (the static list, referenced via the class
name) vs. `Model.CustomerOptions` (the instance property) — don't conflate
the two.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test MaterialLedger.Tests --filter PEP_nest_browser_status_filter_is_a_dropdown_defaulting_to_To_be_cut`
Expected: PASS

- [ ] **Step 6: Run the full existing PepNests test suite to check for regressions**

Run: `dotnet test MaterialLedger.Tests --filter PepNestPresentationTests`
Expected: All PASS, including `PEP_nest_browser_exposes_filters_and_a_details_route` (still checks `asp-for="Status"`, which is present on the `<select>` too).

- [ ] **Step 7: Commit**

```bash
git add MaterialLedger.Web/Pages/PepNests/Index.cshtml MaterialLedger.Web/Pages/PepNests/Index.cshtml.cs MaterialLedger.Tests/PepNestPresentationTests.cs
git commit -m "feat: make PEP nest status filter a dropdown defaulting to To be cut"
```

---

## Task 2: "Has Been Cut" warning banner on the Details page

**Files:**
- Modify: `MaterialLedger.Web/Pages/PepNests/Details.cshtml`
- Modify: `MaterialLedger.Web/wwwroot/css/app.css`
- Test: `MaterialLedger.Tests/PepNestPresentationTests.cs`

**Interfaces:**
- Consumes: `PepNestDetails.Status` (`string?`, `MaterialLedger.Web/Services/PepNestQueryService.cs:81`) — already exposed as `Model.Nest.Status` on the Details page.
- Produces: `.warning-banner` CSS class in `app.css`, used only by this banner.

- [ ] **Step 1: Write the failing test**

Add to `MaterialLedger.Tests/PepNestPresentationTests.cs` (after the new Task 1 test):

```csharp
    [Fact]
    public void PEP_nest_details_shows_a_warning_when_the_program_has_been_cut()
    {
        var page = ReadPage("PepNests", "Details.cshtml");

        Assert.Contains("warning-banner", page);
        Assert.Contains("Contains(\"has been cut\", StringComparison.OrdinalIgnoreCase)", page);
        Assert.Contains("already been cut", page);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MaterialLedger.Tests --filter PEP_nest_details_shows_a_warning_when_the_program_has_been_cut`
Expected: FAIL (none of these strings exist in `Details.cshtml` yet).

- [ ] **Step 3: Add the warning banner to `Details.cshtml`**

In `MaterialLedger.Web/Pages/PepNests/Details.cshtml`, insert a new block
between the closing `</header>` (line 21) and the existing
`@if (Model.Error is not null) { ... }` line (line 23):

```html
    </header>

    @if (Model.Nest.Status?.Contains("has been cut", StringComparison.OrdinalIgnoreCase) == true)
    {
        <p class="warning-banner" role="alert">⚠ This program has already been cut.</p>
    }

    @if (Model.Error is not null) { <p class="form-error" role="alert">@Model.Error</p> }
```

- [ ] **Step 4: Add the `.warning-banner` style to `app.css`**

In `MaterialLedger.Web/wwwroot/css/app.css`, immediately after the
`.alert-error` rule (currently lines 473-477), add:

```css
.warning-banner {
  background: #fff4d6;
  border: 2px solid #e0a300;
  color: #7a5200;
  font-size: 1.15rem;
  font-weight: 700;
  padding: 1rem 1.25rem;
  margin-bottom: 1.25rem;
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test MaterialLedger.Tests --filter PEP_nest_details_shows_a_warning_when_the_program_has_been_cut`
Expected: PASS

- [ ] **Step 6: Run the full existing PepNests test suite to check for regressions**

Run: `dotnet test MaterialLedger.Tests --filter PepNestPresentationTests`
Expected: All PASS, including
`PEP_nest_details_shows_material_mapping_and_hands_off_to_a_material_hold`.

- [ ] **Step 7: Commit**

```bash
git add MaterialLedger.Web/Pages/PepNests/Details.cshtml MaterialLedger.Web/wwwroot/css/app.css MaterialLedger.Tests/PepNestPresentationTests.cs
git commit -m "feat: warn on PEP nest details page when a program has already been cut"
```

---

## Task 3: Manual verification

**Files:** none (verification only)

- [ ] **Step 1: Build and run the app**

Run: `dotnet run --project MaterialLedger.Web`

- [ ] **Step 2: Verify the status dropdown default**

Navigate to `/PepNests` with no query string. Confirm the Status field is a
`<select>` showing "To be cut" selected, the results are filtered
accordingly, and selecting "All statuses" and clicking "Find nests" clears
the filter (shows nests of every status).

- [ ] **Step 3: Verify the cut warning**

Using the Status dropdown, filter to "Has been cut" (or another status
containing "has been cut"), open one of the resulting nests' Details page,
and confirm the amber warning banner "⚠ This program has already been cut."
renders directly under the page heading, above the PEP material section.
Then open a nest with a different status (e.g. "To be cut") and confirm the
banner does not render.

- [ ] **Step 4: No commit for this task** — verification only, nothing to
commit.
