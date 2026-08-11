# PEP Nests — Status Dropdown and "Has Been Cut" Warning — Design

Date: 2026-08-11

## Purpose

On `/PepNests`, the status filter is a free-text `<input>` with an
autocomplete `<datalist>` — nothing tells the user what values are valid, so
they have to guess or open the dropdown-like suggestion list themselves.
Separately, when viewing a single nest on the Details page, there's no
indication that a program has already been cut, which risks someone acting on
it as if it still needed cutting. This change makes the status filter a real
`<select>` dropdown defaulting to "To be cut," and adds a prominent warning
banner on the Details page when a nest's status indicates it's already been
cut.

## Scope

- `MaterialLedger.Web/Pages/PepNests/Index.cshtml` (+ `.cshtml.cs`): status
  filter becomes a `<select>`, defaults to "To be cut" on first visit.
- `MaterialLedger.Web/Pages/PepNests/Details.cshtml`: warning banner when the
  nest's status indicates it's been cut.
- `MaterialLedger.Web/wwwroot/css/app.css`: new `.warning-banner` style.
- Test coverage in `MaterialLedger.Tests/PepNestPresentationTests.cs`.
- Out of scope: editing a nest's status (PEP is the system of record; this
  app only reads nest data), changing how `PepNestQueryService` talks to the
  PEP API.

## Status values

The authoritative list comes from `PepApi.Core`'s `PepHelper.GetStatus(int)`
(`C:\Users\aisaacs\Desktop\Projects\PepApi.Core\PepApi.Core\PepHelper.cs:84-101`),
which is what actually populates `NestSummary.Status` /
`NestDetails.Status` over the wire:

| Value | String |
|---|---|
| -1 | Deleted |
| 0 | To be cut |
| 1 | Quote |
| 2 | Has been cut |
| 3 | Hold |
| 4 | Quote, accepted, to be cut |
| 5 | Quote, accepted, has been cut |
| 6 | Quote, accepted, hold |
| 7 | Quote, accepted, moved to wip |
| 8 | Quote, rejected |
| 9 | Quote, timed out |
| other | N/A |

`NestFilterData.Apply` on the PEP API side (`PepApi.Core/Models/NestFilterData.cs:80-84`)
matches `Status` case-insensitively and exactly, except the literal value
`"All"` bypasses the filter entirely. The dropdown will use its own empty
value for "all" rather than relying on PEP's `"All"` sentinel, since
`PepNestQueryService.GetNestsAsync` already omits the query parameter
entirely when `status` is null/whitespace
(`MaterialLedger.Web/Services/PepNestQueryService.cs:13`) — same effect, no
new special-casing needed.

These 12 strings are hardcoded as a `static readonly` list in `IndexModel`
rather than fetched — they're a fixed enum on the PEP side, not
year-dependent data, so hardcoding is simpler and doesn't add a runtime call.
`IndexModel` stops reading `.Statuses` off `GetFilterOptionsAsync`'s result;
`PepNestQueryService.GetFilterOptionsAsync` and `PepNestFilterOptions` are
left as-is (no service signature changes) since `Customers` is still read
from the same call for the customer filter, which stays unchanged.

## Index page (`MaterialLedger.Web/Pages/PepNests/Index.cshtml.cs`)

```csharp
[BindProperty(SupportsGet = true)] public string? Status { get; set; } = "To be cut";
```

Default applies only when the `Status` query key is absent entirely (a bare
`/PepNests` visit) — ASP.NET model binding leaves the C# default in place in
that case. Submitting the filter form always includes the `Status` key (even
"All statuses", value `""`), so an explicit user choice always overrides the
default; the "old" no-filter behavior is one click away via "All statuses."

```csharp
public static readonly IReadOnlyList<string> StatusOptions =
[
    "To be cut", "Quote", "Has been cut", "Hold",
    "Quote, accepted, to be cut", "Quote, accepted, has been cut",
    "Quote, accepted, hold", "Quote, accepted, moved to wip",
    "Quote, rejected", "Quote, timed out", "Deleted", "N/A",
];
```

Remove the old `StatusOptions` property that came from
`GetFilterOptionsAsync(Year).Statuses` and the corresponding line in
`OnGetAsync`; keep the `Customer` half unchanged.

## Index page (`MaterialLedger.Web/Pages/PepNests/Index.cshtml`)

Replace:

```html
<label>Status <input asp-for="Status" list="statusOptions" placeholder="To be cut" autocomplete="off" /></label>
...
<datalist id="statusOptions">
    @foreach (var status in Model.StatusOptions) { <option value="@status"></option> }
</datalist>
```

with:

```html
<label>Status
    <select asp-for="Status">
        <option value="">All statuses</option>
        @foreach (var status in MaterialLedger.Web.Pages.PepNests.IndexModel.StatusOptions)
        {
            <option value="@status">@status</option>
        }
    </select>
</label>
```

`asp-for="Status"` handles marking the matching `<option>` as selected
(case-sensitive against the exact strings above, which is fine since both
the default and every option come from the same fixed list). Submitting the
form still round-trips through `GET`, unchanged.

## Details page — "Has Been Cut" warning

In `Details.cshtml`, add a warning immediately after the `page-heading`
`<header>` (before "PEP material" and every other section), shown whenever
`Model.Nest.Status` contains "has been cut" case-insensitively — this covers
both the plain `"Has been cut"` and the compound
`"Quote, accepted, has been cut"`, since both mean the program has
physically been cut:

```html
@if (Model.Nest.Status?.Contains("has been cut", StringComparison.OrdinalIgnoreCase) == true)
{
    <p class="warning-banner" role="alert">⚠ This program has already been cut.</p>
}
```

## CSS (`MaterialLedger.Web/wwwroot/css/app.css`)

New `.warning-banner` class, visually louder than the existing quiet
1px-border banners (`.info-banner`, `.form-error`, `.alert-success`,
`.alert-error` at lines 449-477) so it reads as urgent at a glance:

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

## Testing

Extend `MaterialLedger.Tests/PepNestPresentationTests.cs` (existing
pattern: read the raw `.cshtml`/`.cs` file text, assert on markup/strings —
no PageModel execution needed since none of this touches handler logic):

- Index page renders `<select asp-for="Status">` (not `<input ... list=`)
  and contains `"To be cut"` and `"Has been cut"` as option values.
- `IndexModel.cs` source contains `= "To be cut"` as the `Status` default.
- Details page source contains `warning-banner` and the `"has been cut"`
  case-insensitive check.

## Manual verification

Run the app, visit `/PepNests` fresh (no query string) and confirm the
Status dropdown shows "To be cut" selected and the list is filtered.
Visit a nest whose status is "Has been cut" (or find one via the dropdown)
and confirm the warning banner renders at the top of the Details page.
