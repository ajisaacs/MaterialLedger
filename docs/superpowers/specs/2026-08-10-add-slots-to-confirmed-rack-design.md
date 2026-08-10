# Add slots to an already-confirmed rack — design

## Context

`Locations/ConfirmSlots` (and its backing `LocationSlotService.ConfirmLocationSlotsAsync`) is a
one-time initial setup action: pick a rack, specify a slot count and starting number, and it
creates that many child `Shelf`/`Bay`/`Staging` locations and marks the rack `CapacityConfirmed`.
`Locations/Details.cshtml.cs`'s `CanConfirmSlots` is `!Rack.CapacityConfirmed && (role check)`, so
the link to that page disappears entirely once a rack has been confirmed once.

In practice, a rack's real slot count is sometimes undercounted the first time — e.g.
`L1-SOUTH-ROLL` was confirmed with 4 slots (`S01`–`S04`), but a top shelf that's actually used for
storage wasn't counted. There is currently no way to add that missing slot without going around the
UI (direct SQL).

## Non-goals

- Renaming an existing slot's code/display name.
- Deactivating or removing a slot.
- Changing a rack's storage mode or any other rack-level field.

These are all real "edit" capabilities but weren't asked for — only "add more slots to a rack that's
already confirmed" is in scope. A true edit/remove capability is a separate future feature if it
ever comes up.

## Data flow

**No service-layer change.** `LocationSlotService.ConfirmLocationSlotsAsync` already supports this:
it never checks `parent.CapacityConfirmed`, only that the specific requested slot codes don't
already exist (`LocationSlotConflictException` otherwise), and it always sets
`parent.CapacityConfirmed = true` (a no-op write if already true). Calling it again with a fresh
`StartingNumber` for an already-confirmed rack already produces the correct result today — the gap
is entirely that the UI never offers this path.

## UI changes

### `Locations/Details.cshtml.cs` / `.cshtml`

- Rename `CanConfirmSlots` → `CanManageSlots`: `(User.IsInRole("Programmer") || User.IsInRole("Manager"))`,
  no longer gated on `!Rack.CapacityConfirmed`.
- The action link's text switches on `Rack.CapacityConfirmed`: "Confirm slot count" (not yet
  confirmed) or "Add slots" (already confirmed) — same target page (`ConfirmSlots`) either way.
- "Printable labels" becomes visible whenever `Rack.CapacityConfirmed` is true, independent of
  `CanManageSlots` (previously it was the `else` branch of the same `if`, so a Programmer/Manager
  viewing an already-confirmed rack could never see it — that was very likely a pre-existing
  oversight, not intentional, since the only page offering slot management showed either the confirm
  link or the labels link, never neither nor both).

### `Locations/ConfirmSlots.cshtml.cs` / `.cshtml`

- `OnGetAsync` now also loads whether the rack `IsConfirmed = Rack.CapacityConfirmed` (new
  read-only model property) so the view can switch its copy and fields.
- When `IsConfirmed` is true:
  - Heading reads "Add slots to {DisplayName}" instead of "Confirm slots for {DisplayName}".
  - The "Starting number" input is not rendered — `StartingNumber` is not bound from the form
    in this mode at all.
  - Submit button reads "Add slots" instead of "Create slots".
- `OnPostAsync`, when the rack (re-loaded fresh, same as today) has `CapacityConfirmed == true`:
  ignores any posted `StartingNumber` and instead computes it server-side as
  `(existing children's max SortOrder, or 0, if none) + 1`, queried fresh at POST time — never
  trusted from a hidden field, so two concurrent "add slots" submissions can't collide on numbering
  (the existing `LocationSlotConflictException` path is the backstop if they still do — e.g. two
  admins racing to add "the next slot" at the same moment, one loses the race and gets a clear
  error to resubmit).
  When `CapacityConfirmed == false`, behavior is byte-for-byte unchanged from today (manual
  `StartingNumber`, existing validation).
- `SlotCount` validation (1–200) is unchanged and applies in both modes.

## Testing

`MaterialLedger.Tests/LocationSlotServiceTests.cs` already covers `ConfirmLocationSlotsAsync`
directly (unchanged, since the service itself doesn't change) — add one test confirming that
calling it a second time with a fresh `StartingNumber` against an already-`CapacityConfirmed` rack
succeeds and appends rather than throwing, to lock in the exact behavior this feature now relies on.

No new presentation/page tests planned — this codebase's existing presentation-test pattern
(`NavigationPresentationTests`, `ReceivingEntryPresentationTests`) is used to lock down specific
regressions, not as blanket per-page coverage, and this change doesn't touch anything those existing
suites assert on.
