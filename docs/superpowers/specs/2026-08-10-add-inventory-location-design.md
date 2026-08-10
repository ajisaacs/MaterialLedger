# Add inventory location — design

## Context

`InventoryLocation` rows (Areas and Racks) are currently only created by
`MaterialLedger.Web/Services/LocationSeeder.cs` — a hardcoded list applied at startup.
There is no way to add a new storage location from the web UI; adding one today means
editing the seeder and redeploying. Slot-level children (Shelf/Bay/Staging under a Rack)
already have their own UI flow (`Locations/ConfirmSlots`), so this gap is specifically
about the two manually-defined tiers: Area and Rack.

## Approach

Follow the existing idempotent-service + thin-page pattern used by
`MaterialLedger.Data/Locations/LocationSlotService.cs` and
`Pages/Locations/ConfirmSlots.cshtml(.cs)`:

- A new `LocationService.CreateLocationAsync` method does validation and persistence
  inside a serializable transaction, guarded by `OperationLogGuard` for idempotent replay
  on a repeated correlation id — the same mechanics `ConfirmLocationSlotsAsync` already
  uses.
- A new `Locations/Create` Razor Page is a thin wrapper: binds form fields, calls the
  service, surfaces `InvalidOperationException`/`OperationConflictException` as a page
  error, same as `ConfirmSlots.cshtml.cs`.

This avoids introducing a new pattern for what is, structurally, the same kind of
operation (idempotent creation of one or more `InventoryLocation` rows) already solved
for slots.

## Backend: `LocationService.CreateLocationAsync`

New file `MaterialLedger.Data/Locations/LocationService.cs`.

```csharp
public record CreateLocationRequest(
    string LocationCode,
    string DisplayName,
    InventoryLocationType LocationType,   // Area or Rack
    InventoryStorageMode StorageMode,     // ignored for Area
    long? ParentInventoryLocationId);     // null for Area, required for Rack

public class LocationService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    public async Task<InventoryLocation> CreateLocationAsync(
        Guid correlationId, CreateLocationRequest request, int executedBy) { ... }
}
```

Behavior:

- Wrapped in `db.Database.BeginTransactionAsync(IsolationLevel.Serializable)`, with
  `operationLog.CheckAsync`/`Record` for replay-on-duplicate-correlation-id, matching
  `ConfirmLocationSlotsAsync`.
- **Code normalization**: trim, uppercase. Reject if empty or containing characters
  outside `A-Z0-9-` (`InvalidOperationException`, message states the allowed charset).
- **Duplicate check**: reject if `LocationCode` already exists.
- **Display name**: reject if empty/whitespace.
- **Location type restriction**: only `Area` or `Rack` accepted (the page's `<select>`
  only offers these two, but the service validates independently of the caller).
- **Parent rules**:
  - `Area` → `ParentInventoryLocationId` must be null (top-level).
  - `Rack` → `ParentInventoryLocationId` must reference an existing, active location
    with `LocationType == Area`; otherwise `InvalidOperationException`.
- **Storage mode**: for `Area`, stored as `InventoryStorageMode.Other` regardless of
  input (unused at that tier, matches seeder convention). For `Rack`, stored as given.
- **Sort order**: computed as `(max SortOrder among existing rows sharing the same
  ParentInventoryLocationId, or -1 if none) + 1`. No field exposed for this.
- **Defaults**: `CapacityConfirmed = false`, `IsActive = true` — matches
  `LocationSeeder.ToEntity`.
- Returns the created `InventoryLocation`.

## Frontend: `Locations/Create` page

New `Pages/Locations/Create.cshtml` + `Create.cshtml.cs`.

- `[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]` — same
  policy as `ConfirmSlotsModel`.
- `CreateModel` binds: `LocationCode`, `DisplayName`, `LocationType`, `StorageMode`,
  `ParentInventoryLocationId` (nullable long), plus a hidden `CorrelationId`
  (`Guid.NewGuid()` set in `OnGetAsync`, resubmitted on `OnPostAsync`).
- `OnGetAsync` also loads `AvailableAreas` — `db.InventoryLocations.Where(x =>
  x.LocationType == InventoryLocationType.Area && x.IsActive).OrderBy(x =>
  x.DisplayName)` — for the parent-area dropdown.
- Form:
  - Location code — text input.
  - Display name — text input.
  - Location type — `<select>`: `Area` / `Rack`.
  - Parent area — `<select>` from `AvailableAreas` plus a `-- none --` option; shown
    only when `Rack` is selected (client-side JS toggles visibility/required-ness on
    `change` of the type select, mirroring the conditional-section pattern already used
    in `Reservations/Create.cshtml`).
  - Storage mode — `<select>` (`RolloutShelf` / `VerticalDropBay` / `FloorStaging` /
    `Other`); shown only when `Rack` is selected.
- `OnPostAsync` calls `LocationService.CreateLocationAsync`, catches
  `InvalidOperationException` / `OperationConflictException` into `Error` and
  re-renders the page on failure (same as `ConfirmSlotsModel.OnPostAsync`).
- On success: redirect to `Locations/Details?code={code}` if the created location is a
  Rack (so the user can immediately confirm slots); redirect to `Locations/Index` if it
  is an Area (`Details` expects a rack with slots and isn't meaningful for an Area).

### Navigation

`Pages/Locations/Index.cshtml`: add an "Add location" link near the page heading,
rendered only when `User.IsInRole("Programmer") || User.IsInRole("Manager")` — same
role check style as `CanConfirmSlots` in `Details.cshtml.cs`. `IndexModel` gains a
`CanAddLocation` bool set the same way.

## Out of scope

- No edit/deactivate UI for existing locations — creation only.
- No changes to `LocationSlotService`, `ConfirmSlots`, or the slot-confirmation flow.
- No changes to `LocationSeeder` — it continues to seed the seven original locations at
  startup; this page is for locations added afterward.
- Areas created here don't get a `Details` view of their own (no rack/slot data to
  show) — they'll simply appear as parent options in the Rack dropdown and, if desired
  later, in a future areas-listing view. Not building that view now (YAGNI).

## Testing / verification plan

1. New tests in `MaterialLedger.Tests` (mirroring `LocationSlotServiceTests`), covering
   `LocationService.CreateLocationAsync`:
   - Creates an Area with no parent.
   - Creates a Rack under an existing Area, storage mode persisted correctly.
   - Normalizes/uppercases a lowercase or mixed-case code.
   - Rejects a code with invalid characters (e.g. spaces, lowercase-after-normalization
     still containing symbols outside `A-Z0-9-`).
   - Rejects a duplicate code.
   - Rejects a Rack with no parent.
   - Rejects a Rack whose parent is not an Area (e.g. parent is another Rack).
   - Rejects an Area with a parent specified.
   - Sort order is `max(sibling SortOrder) + 1`, and `0` for the first child of a given
     parent (or first top-level Area).
   - Replays the prior result when called again with the same correlation id
     (idempotency), matching `LocationSlotServiceTests`' coverage of
     `ConfirmLocationSlotsAsync`.
2. `dotnet build` succeeds.
3. Manual/Playwright walkthrough as a Programmer or Manager user:
   - `/Locations` shows "Add location" link; not shown for an Operator-only user.
   - Create an Area → redirected to `/Locations` (no Details view for an Area).
   - Create a Rack under that Area → redirected to `/Locations/Details/{code}`; rack
     appears in the `/Locations` table with "Slots confirmed: No".
   - Attempt a duplicate code → page re-renders with an error, no duplicate row created.
   - Confirm the new Rack can immediately go through the existing "Confirm slots" flow.
