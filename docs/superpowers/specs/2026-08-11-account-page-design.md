# Account Page — Design

Date: 2026-08-11

## Purpose

Give any signed-in user a self-service page to update their display name and
change their own password, without needing an admin or direct database
access. Today there is no way for a user to do either.

## Scope

- A new `/Account` Razor Page, reachable by any authenticated user (no role
  restriction).
- Two independent actions on the page:
  1. Update display name.
  2. Change password (requires current password).
- Username (the login id) is **not** editable here — out of scope.
- No "forgot password" / admin-reset flow — out of scope.

## Data model

No schema changes. `AppUser` already has `DisplayName` and `PasswordHash`
(`MaterialLedger.Data\Models\AppUser.cs`).

## Page: `MaterialLedger.Web/Pages/Account.cshtml` + `Account.cshtml.cs`

`AccountModel(MaterialLedgerDbContext db, PasswordHasher<AppUser> hasher) : PageModel`

- `[Authorize]` — no policy restriction; every signed-in role can reach it.
- Loads the current `AppUser` in `OnGetAsync` via
  `User.FindFirst(ClaimTypes.NameIdentifier)` (same pattern as
  `Locations/Create.cshtml.cs`), populates `DisplayName` for display.

Two Razor Pages **named handlers**, each with its own bound properties and
error string so submitting one form doesn't clobber the other's state:

### `OnPostProfileAsync` (handler = "Profile")

- `[BindProperty] public string DisplayName`
- Validates non-empty (trim). On failure: `ProfileError = "Display name is required."`, return `Page()`.
- Updates `AppUser.DisplayName`, saves.
- Because the auth cookie's `ClaimTypes.Name` claim was set at login time
  (`Login.cshtml.cs`) and won't reflect the change until next login, re-issue
  the cookie immediately: rebuild the same claims set used in
  `LoginModel.OnPostAsync` (`NameIdentifier`, `Name`, `Role`) with the new
  `DisplayName` and call `HttpContext.SignInAsync` again. This keeps the
  sidebar's `@User.Identity.Name` correct without forcing a re-login.
- Sets `ProfileSuccess = true` so the page can show a confirmation message.
- Returns `Page()` (no redirect — stay on the page, matches the "no redirect
  needed for a same-page save" feel; redirecting would also require
  round-tripping success state through TempData for no benefit here).

### `OnPostPasswordAsync` (handler = "Password")

- `[BindProperty] public string CurrentPassword`
- `[BindProperty] public string NewPassword`
- `[BindProperty] public string ConfirmPassword`
- Validation, in order, first failure wins, sets `PasswordError`, returns `Page()`:
  1. `CurrentPassword` verifies via
     `hasher.VerifyHashedPassword(user, user.PasswordHash, CurrentPassword)`
     (same call used in `AppUserAuthenticator.VerifyAsync`). Failure →
     "Current password is incorrect."
  2. `NewPassword.Length < 8` → "New password must be at least 8 characters."
  3. `NewPassword != ConfirmPassword` → "New password and confirmation do not match."
- On success: `user.PasswordHash = hasher.HashPassword(user, NewPassword)`,
  save. No re-sign-in needed — the session cookie isn't derived from the
  password hash.
- Sets `PasswordSuccess = true`.
- Returns `Page()`.

Both handlers re-load `DisplayName` from the DB before returning `Page()` on
a validation failure, so the profile field doesn't go blank if the password
form is the one that failed (and vice versa) — each handler only touches its
own bound properties.

## View: `Account.cshtml`

Follows the `Locations/Create.cshtml` structural pattern (plain `<form>` +
`<fieldset>`, antiforgery token, inline error paragraph). Two forms, each
posting to a named handler via `asp-page-handler`:

```html
<form method="post" asp-page-handler="Profile">
    @Html.AntiForgeryToken()
    <fieldset>
        <legend>Profile</legend>
        @if (Model.ProfileError is not null) { <p class="form-error">@Model.ProfileError</p> }
        @if (Model.ProfileSuccess) { <p class="form-success">Display name updated.</p> }
        <label>Display name <input asp-for="DisplayName" maxlength="160" /></label>
        <button type="submit">Save</button>
    </fieldset>
</form>

<form method="post" asp-page-handler="Password">
    @Html.AntiForgeryToken()
    <fieldset>
        <legend>Change password</legend>
        @if (Model.PasswordError is not null) { <p class="form-error">@Model.PasswordError</p> }
        @if (Model.PasswordSuccess) { <p class="form-success">Password changed.</p> }
        <label>Current password <input asp-for="CurrentPassword" type="password" autocomplete="current-password" /></label>
        <label>New password <input asp-for="NewPassword" type="password" autocomplete="new-password" /></label>
        <label>Confirm new password <input asp-for="ConfirmPassword" type="password" autocomplete="new-password" /></label>
        <button type="submit">Change password</button>
    </fieldset>
</form>
```

`form-error` already exists as a CSS class (used by `Login.cshtml`);
`form-success` is new and needs a small addition to `app.css` (same shape,
success color).

## Nav change: `Pages/Shared/_Layout.cshtml`

The account widget currently renders the user's name as plain text:

```html
<div><strong>@User.Identity.Name</strong><small>@User.FindFirst(...ClaimTypes.Role)?.Value</small></div>
```

Wrap the name in a link to `/Account`:

```html
<div><a class="account-name" asp-page="/Account"><strong>@User.Identity.Name</strong></a><small>@User.FindFirst(...ClaimTypes.Role)?.Value</small></div>
```

## Error handling

- All validation is server-side, following the existing `Error` string /
  `<p class="form-error">` convention used throughout the app (e.g.
  `Locations/Create.cshtml.cs`, `Login.cshtml.cs`) — no client-side JS
  validation needed.
- No new exception types; this is a straightforward read-validate-save flow
  with no concurrency/idempotency concerns (unlike `PurchaseOrderService`,
  there's no multi-step domain operation here, so no `CorrelationId` /
  `OperationConflictException` machinery is needed).

## Testing

Integration test in `MaterialLedger.Tests`, following the
`ReceiveAllIntegrationTests.cs` pattern (seed a user directly in the DB,
construct the PageModel with a fake `ClaimsPrincipal` via `PageContext`,
call the handler methods directly):

- `OnPostProfileAsync`: updates `DisplayName` in the DB; rejects blank name.
- `OnPostPasswordAsync`: rejects wrong current password; rejects short new
  password; rejects mismatched confirmation; on success, the new password
  verifies via `PasswordHasher` and the old one no longer does.

No browser/Playwright test — this is a plain form flow well covered by
PageModel-level integration tests, consistent with how `ReceiveAll` was
tested.
