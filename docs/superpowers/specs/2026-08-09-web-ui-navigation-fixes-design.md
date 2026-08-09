# Web UI navigation-gap fixes — design

## Context

The recent (uncommitted) redesign of `Index.cshtml`, `Login.cshtml`, and `_Layout.cshtml`
fixed the visual layer (sidebar nav, dashboard cards, styled login) but left several
navigation dead ends in the pages beneath it. These were found by driving the running
app as an Operator user with Playwright against the real dev database (`forge`,
`MaterialLedger` schema) and are the actual source of "the web UI is non-intuitive,"
not the visual styling.

Confirmed gaps:

1. `/Reservations` (primary nav item) has no link to `/Reservations/Create`. The only
   path to creating a reservation is via `/Inventory/Search` → "Reserve" on a specific
   row. A user who lands on Reservations first has no way forward.
2. `/Reservations/Create` (`Pages/Reservations/Create.cshtml.cs`) accepts an optional
   `{inventoryItemId:long?}` route parameter but has no item picker of its own. Reached
   without an id, it renders a form bound to no item.
3. `/Reservations`, `/Closeout`, and `/Exceptions` render a bare table header with zero
   rows when there's no data — no distinguishing "empty" from "broken."
4. `Locations/Details.cshtml` lists each slot's occupying item as plain text, with no
   link back to Inventory Search or a reserve action, even though `Inventory/Search`
   already supports filtering by `LocationCode`.

## Approach

Reuse existing patterns rather than building new UI:

- `Inventory/Search` is already the one page with full filtering (family, grade,
  thickness, kind, size, location code, availability) and is already the entry point
  for reserving a specific item (`Reserve` link → `Reservations/Create`). Point every
  other "I want to reserve/find material" moment at it instead of duplicating a picker.
- Follow the existing `.content table` / `.eyebrow` CSS vocabulary in `app.css` for the
  empty-state message so it looks like part of the same design system, not a new one.

## Changes

### 1. Reservations entry point

`Pages/Reservations/Index.cshtml`: add a link/button near the page heading —
"Search inventory to reserve material →" — pointing at `/Inventory/Search`. Present
whether the list is empty or populated.

`Pages/Reservations/Create.cshtml.cs`: in `OnGetAsync`, if `InventoryItemId` is null or
the item can't be found, redirect to `/Inventory/Search` with a `TempData["Message"]`
of "Select an item from Inventory Search to create a reservation." instead of rendering
the page. `Pages/Inventory/Search.cshtml` reads and displays `TempData["Message"]` at
the top of the page, if present, using a new `.info-banner` CSS rule in `app.css` —
same shape/padding as the existing `.form-error` banner but neutral (blue/gray) coloring
instead of red, since this is guidance, not an error.

### 2. Empty states

Add a `.empty-state` CSS rule to `app.css` (muted text, padded, matches `.content`
typography). In `Reservations/Index.cshtml`, `Closeout/Index.cshtml`, and
`Exceptions/Index.cshtml` (both the Open and Recently Resolved sections), render a
`<p class="empty-state">` message in place of the table when the corresponding list is
empty — e.g. "No open reservations." / "Nothing awaiting closeout." / "No open
exceptions." / "No recently resolved exceptions."

### 3. Location map → inventory link

`Pages/Locations/Details.cshtml`: wrap each occupying item's summary text in
`<a asp-page="/Inventory/Search" asp-route-LocationCode="@slot.LocationCode">`, so
clicking it opens Inventory Search pre-filtered to that location.

## Out of scope

- No changes to `MaterialExceptionService`, `CloseoutService`, or any other data/service
  layer code — this is presentation-only.
- No new automated page-level tests are added, matching the existing test suite's scope
  (service/data layer only, via `MaterialLedger.Tests`). Verification is manual: build,
  run the dev server, and click through each fixed flow with Playwright, the same way
  the gaps were found.

## Testing / verification plan

1. `dotnet build` succeeds.
2. Run the dev server (`dotnet run` against the `forge` dev database, already configured
   via `dotnet user-secrets`).
3. Playwright walkthrough as the Operator user:
   - Land on `/Reservations` with an empty list → see empty-state message and the
     "Search inventory" link; follow it to `/Inventory/Search`.
   - Navigate directly to `/Reservations/Create` with no id → redirected to
     `/Inventory/Search` with the explanatory message visible.
   - Reserve an item via the existing `Inventory/Search` → `Reserve` flow → confirm
     `Reservations/Create` still renders normally with a valid id (regression check).
   - `/Closeout` and `/Exceptions` with empty data → empty-state messages render.
   - `/Locations/Details/{code}` → click an item's summary text → lands on
     `/Inventory/Search` filtered to that location code.
