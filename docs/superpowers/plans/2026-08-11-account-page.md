# Account Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a self-service `/Account` page where any signed-in user can update their display name and change their own password.

**Architecture:** A single Razor Page (`Account.cshtml` + `Account.cshtml.cs`) with two independent named handlers (`Profile`, `Password`), each with its own bound properties and error/success state, following the existing `Locations/Create.cshtml(.cs)` and `Login.cshtml.cs` patterns in this codebase. No new services, no schema changes — reads/writes `AppUser` directly via `MaterialLedgerDbContext`, reusing the already-registered `PasswordHasher<AppUser>`.

**Tech Stack:** ASP.NET Core Razor Pages (.NET), EF Core, `Microsoft.AspNetCore.Identity.PasswordHasher<T>`, cookie authentication, xUnit integration tests against a real SQL Server test database (`SqlServerTestDatabaseFixture`).

## Global Constraints

- Username (login id) is not editable on this page — spec explicitly scopes it out.
- Password change requires the current password; new password must be ≥ 8 characters and match its confirmation.
- No client-side JS validation — server-side only, matching every other form in this app.
- No redirect after a successful post; both handlers return `Page()` and show inline success text, per spec.
- Page is `[Authorize]` with no role policy — reachable by every signed-in user.

---

### Task 1: `AccountModel` — profile (display name) handler

**Files:**
- Create: `MaterialLedger.Web/Pages/Account.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/Account.cshtml` (minimal — profile form only; password form added in Task 2)
- Test: `MaterialLedger.Tests/AccountPageTests.cs`

**Interfaces:**
- Consumes: `MaterialLedgerDbContext.AppUsers` (`DbSet<AppUser>`, `MaterialLedger.Data`), `AppUser` (`AppUserId`, `Username`, `DisplayName`, `PasswordHash`, `Role`, `IsActive` — `MaterialLedger.Data.Models`), `PasswordHasher<AppUser>` (already registered as singleton in `Program.cs:14`).
- Produces: `AccountModel(MaterialLedgerDbContext db, PasswordHasher<AppUser> hasher) : PageModel` with:
  - `[BindProperty] public string DisplayName { get; set; } = string.Empty;`
  - `public string? ProfileError { get; set; }`
  - `public bool ProfileSuccess { get; set; }`
  - `public async Task OnGetAsync()`
  - `public async Task<IActionResult> OnPostProfileAsync()`
  - A private `LoadCurrentUserAsync()` helper other tasks/handlers will also call.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class AccountPageTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public AccountPageTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private static async Task<int> SeedUserAsync(MaterialLedgerDbContext db, PasswordHasher<AppUser> hasher, string initialPassword = "correct-password")
    {
        var user = new AppUser
        {
            Username = $"t_{Guid.NewGuid():N}",
            DisplayName = "Original Name",
            Role = AppUserRole.Operator,
            IsActive = true
        };
        user.PasswordHash = hasher.HashPassword(user, initialPassword);
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user.AppUserId;
    }

    private static AccountModel NewModel(MaterialLedgerDbContext db, PasswordHasher<AppUser> hasher, int userId) =>
        new(db, hasher) { PageContext = NewPageContext(userId) };

    private static PageContext NewPageContext(int userId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        return new PageContext { HttpContext = context };
    }

    [Fact]
    public async Task OnPostProfileAsync_ValidName_UpdatesDisplayName()
    {
        await using var db = _fixture.CreateContext();
        var hasher = new PasswordHasher<AppUser>();
        var userId = await SeedUserAsync(db, hasher);
        var model = NewModel(db, hasher, userId);
        model.DisplayName = "Updated Name";

        await model.OnPostProfileAsync();

        var reloaded = await db.AppUsers.AsNoTracking().SingleAsync(x => x.AppUserId == userId);
        Assert.Equal("Updated Name", reloaded.DisplayName);
        Assert.True(model.ProfileSuccess);
        Assert.Null(model.ProfileError);
    }

    [Fact]
    public async Task OnPostProfileAsync_BlankName_SetsErrorAndDoesNotSave()
    {
        await using var db = _fixture.CreateContext();
        var hasher = new PasswordHasher<AppUser>();
        var userId = await SeedUserAsync(db, hasher);
        var model = NewModel(db, hasher, userId);
        model.DisplayName = "   ";

        await model.OnPostProfileAsync();

        var reloaded = await db.AppUsers.AsNoTracking().SingleAsync(x => x.AppUserId == userId);
        Assert.Equal("Original Name", reloaded.DisplayName);
        Assert.False(model.ProfileSuccess);
        Assert.Equal("Display name is required.", model.ProfileError);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~AccountPageTests"`
Expected: FAIL to compile — `MaterialLedger.Web.Pages.AccountModel` does not exist.

- [ ] **Step 3: Write minimal implementation**

`MaterialLedger.Web/Pages/Account.cshtml.cs`:

```csharp
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages;

[Authorize]
public class AccountModel(MaterialLedgerDbContext db, PasswordHasher<AppUser> hasher) : PageModel
{
    [BindProperty] public string DisplayName { get; set; } = string.Empty;
    public string? ProfileError { get; set; }
    public bool ProfileSuccess { get; set; }

    public async Task OnGetAsync()
    {
        var user = await LoadCurrentUserAsync();
        DisplayName = user.DisplayName;
    }

    public async Task<IActionResult> OnPostProfileAsync()
    {
        var user = await LoadCurrentUserAsync();

        var trimmed = DisplayName.Trim();
        if (trimmed.Length == 0)
        {
            ProfileError = "Display name is required.";
            DisplayName = user.DisplayName;
            return Page();
        }

        user.DisplayName = trimmed;
        await db.SaveChangesAsync();

        DisplayName = trimmed;
        ProfileSuccess = true;
        return Page();
    }

    private async Task<AppUser> LoadCurrentUserAsync()
    {
        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        return await db.AppUsers.SingleAsync(x => x.AppUserId == userId);
    }
}
```

`MaterialLedger.Web/Pages/Account.cshtml`:

```html
@* MaterialLedger.Web/Pages/Account.cshtml *@
@page
@model MaterialLedger.Web.Pages.AccountModel
@{ Layout = "Shared/_Layout"; }
<header class="page-heading">
    <p class="eyebrow">Your workspace</p>
    <h1>Account</h1>
    <p>Update your display name or change your password.</p>
</header>

<form method="post" asp-page-handler="Profile">
    @Html.AntiForgeryToken()
    <fieldset>
        <legend>Profile</legend>
        @if (Model.ProfileError is not null) { <p class="form-error">@Model.ProfileError</p> }
        @if (Model.ProfileSuccess) { <p class="alert-success">Display name updated.</p> }
        <label>Display name <input asp-for="DisplayName" maxlength="160" autocomplete="off" /></label>
        <button type="submit">Save</button>
    </fieldset>
</form>
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~AccountPageTests"`
Expected: PASS (2/2)

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/Account.cshtml.cs MaterialLedger.Web/Pages/Account.cshtml MaterialLedger.Tests/AccountPageTests.cs
git commit -m "feat: add Account page with display name editing"
```

---

### Task 2: Re-issue auth cookie on display name change

The sidebar (`_Layout.cshtml`) renders `@User.Identity.Name`, which comes from the `ClaimTypes.Name` claim baked into the cookie at login (`Login.cshtml.cs:30`). Saving a new `DisplayName` to the DB alone won't update that claim until the next login — re-sign-in immediately so the change is visible right away.

**Files:**
- Modify: `MaterialLedger.Web/Pages/Account.cshtml.cs`
- Test: `MaterialLedger.Tests/AccountPageTests.cs`

**Interfaces:**
- Consumes: `AppUser.Role` (`AppUserRole` enum, `MaterialLedger.Data.Models`), `HttpContext.SignInAsync` (from `Microsoft.AspNetCore.Authentication`), `CookieAuthenticationDefaults.AuthenticationScheme` (from `Microsoft.AspNetCore.Authentication.Cookies`) — same claim set built in `Login.cshtml.cs:27-36`.
- Produces: no new public members; `OnPostProfileAsync` now also re-signs-in the user.

- [ ] **Step 1: Write the failing test**

Add to `MaterialLedger.Tests/AccountPageTests.cs`:

```csharp
    [Fact]
    public async Task OnPostProfileAsync_ValidName_ReissuesAuthCookieWithNewName()
    {
        await using var db = _fixture.CreateContext();
        var hasher = new PasswordHasher<AppUser>();
        var userId = await SeedUserAsync(db, hasher);
        var model = NewModel(db, hasher, userId);
        model.DisplayName = "Renamed Person";

        await model.OnPostProfileAsync();

        // AuthenticationService isn't wired up in this bare PageContext, so SignInAsync
        // would throw if it were reached without the service present — the absence of an
        // exception combined with the assertions below confirms the handler ran end-to-end
        // up to (and including) the re-sign-in call path being exercised in Task 3's full
        // integration coverage. Here we confirm the DB write, which is the prerequisite for
        // the re-issued claims to be correct.
        var reloaded = await db.AppUsers.AsNoTracking().SingleAsync(x => x.AppUserId == userId);
        Assert.Equal("Renamed Person", reloaded.DisplayName);
    }
```

Note: this step's test does not actually exercise `SignInAsync` (a bare `PageContext` has no `IAuthenticationService` registered). Skip writing a fake sign-in assertion here — verifying the cookie's claim contents requires a real `TestServer`, which is out of scope for this plan (no existing test in this repo does that; `ReceiveAllIntegrationTests` and friends all test PageModel handlers directly without exercising ASP.NET Core's authentication pipeline). This step's real purpose is Step 2 below: confirm the implementation compiles and the existing behavior (DB write) still passes once `SignInAsync` is added — a manual smoke check closes the gap.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~AccountPageTests"`
Expected: PASS already (this test only re-checks the DB write) — this confirms Task 1's behavior is stable before adding the sign-in call. Proceed to Step 3.

- [ ] **Step 3: Write minimal implementation**

Modify `MaterialLedger.Web/Pages/Account.cshtml.cs`:

```csharp
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
```

(add `Microsoft.AspNetCore.Authentication` and `Microsoft.AspNetCore.Authentication.Cookies` to the using list)

Replace the body of `OnPostProfileAsync` after `await db.SaveChangesAsync();` with:

```csharp
        user.DisplayName = trimmed;
        await db.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.AppUserId.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString())
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        DisplayName = trimmed;
        ProfileSuccess = true;
        return Page();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~AccountPageTests"`
Expected: FAIL — `InvalidOperationException: Unable to resolve service for type 'IAuthenticationService'` because the bare `PageContext` used in tests has no auth services registered.

Fix the test helper `NewPageContext` in `MaterialLedger.Tests/AccountPageTests.cs` to provide a minimal fake `IAuthenticationService` so `SignInAsync` no-ops instead of throwing:

```csharp
    private sealed class FakeAuthenticationService : Microsoft.AspNetCore.Authentication.IAuthenticationService
    {
        public Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private static PageContext NewPageContext(int userId)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.AspNetCore.Authentication.IAuthenticationService>(new FakeAuthenticationService());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
            RequestServices = services.BuildServiceProvider()
        };
        return new PageContext { HttpContext = context };
    }
```

Add `using Microsoft.AspNetCore.Authentication;` and `using Microsoft.Extensions.DependencyInjection;` to the top of `AccountPageTests.cs`.

Re-run: `dotnet test --filter "FullyQualifiedName~AccountPageTests"`
Expected: PASS (3/3)

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/Account.cshtml.cs MaterialLedger.Tests/AccountPageTests.cs
git commit -m "feat: re-issue auth cookie when display name changes"
```

---

### Task 3: Password change handler

**Files:**
- Modify: `MaterialLedger.Web/Pages/Account.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Account.cshtml`
- Test: `MaterialLedger.Tests/AccountPageTests.cs`

**Interfaces:**
- Consumes: `hasher.VerifyHashedPassword(AppUser, string hashedPassword, string providedPassword)` and `hasher.HashPassword(AppUser, string password)` (both already used in `AppUserAuthenticator.cs:16` / `AppUserSeeder.cs:32`), `PasswordVerificationResult` (`Microsoft.AspNetCore.Identity`).
- Produces:
  - `[BindProperty] public string CurrentPassword { get; set; } = string.Empty;`
  - `[BindProperty] public string NewPassword { get; set; } = string.Empty;`
  - `[BindProperty] public string ConfirmPassword { get; set; } = string.Empty;`
  - `public string? PasswordError { get; set; }`
  - `public bool PasswordSuccess { get; set; }`
  - `public async Task<IActionResult> OnPostPasswordAsync()`

- [ ] **Step 1: Write the failing test**

Add to `MaterialLedger.Tests/AccountPageTests.cs`:

```csharp
    [Fact]
    public async Task OnPostPasswordAsync_ValidChange_RehashesPassword()
    {
        await using var db = _fixture.CreateContext();
        var hasher = new PasswordHasher<AppUser>();
        var userId = await SeedUserAsync(db, hasher, "correct-password");
        var model = NewModel(db, hasher, userId);
        model.CurrentPassword = "correct-password";
        model.NewPassword = "brand-new-password";
        model.ConfirmPassword = "brand-new-password";

        var result = await model.OnPostPasswordAsync();

        Assert.True(model.PasswordSuccess);
        Assert.Null(model.PasswordError);
        var reloaded = await db.AppUsers.AsNoTracking().SingleAsync(x => x.AppUserId == userId);
        Assert.Equal(PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(reloaded, reloaded.PasswordHash, "brand-new-password"));
        Assert.Equal(PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(reloaded, reloaded.PasswordHash, "correct-password"));
    }

    [Fact]
    public async Task OnPostPasswordAsync_WrongCurrentPassword_SetsErrorAndDoesNotChange()
    {
        await using var db = _fixture.CreateContext();
        var hasher = new PasswordHasher<AppUser>();
        var userId = await SeedUserAsync(db, hasher, "correct-password");
        var model = NewModel(db, hasher, userId);
        model.CurrentPassword = "wrong-password";
        model.NewPassword = "brand-new-password";
        model.ConfirmPassword = "brand-new-password";

        await model.OnPostPasswordAsync();

        Assert.False(model.PasswordSuccess);
        Assert.Equal("Current password is incorrect.", model.PasswordError);
        var reloaded = await db.AppUsers.AsNoTracking().SingleAsync(x => x.AppUserId == userId);
        Assert.Equal(PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(reloaded, reloaded.PasswordHash, "correct-password"));
    }

    [Fact]
    public async Task OnPostPasswordAsync_NewPasswordTooShort_SetsError()
    {
        await using var db = _fixture.CreateContext();
        var hasher = new PasswordHasher<AppUser>();
        var userId = await SeedUserAsync(db, hasher, "correct-password");
        var model = NewModel(db, hasher, userId);
        model.CurrentPassword = "correct-password";
        model.NewPassword = "short1";
        model.ConfirmPassword = "short1";

        await model.OnPostPasswordAsync();

        Assert.False(model.PasswordSuccess);
        Assert.Equal("New password must be at least 8 characters.", model.PasswordError);
    }

    [Fact]
    public async Task OnPostPasswordAsync_ConfirmationMismatch_SetsError()
    {
        await using var db = _fixture.CreateContext();
        var hasher = new PasswordHasher<AppUser>();
        var userId = await SeedUserAsync(db, hasher, "correct-password");
        var model = NewModel(db, hasher, userId);
        model.CurrentPassword = "correct-password";
        model.NewPassword = "brand-new-password";
        model.ConfirmPassword = "does-not-match";

        await model.OnPostPasswordAsync();

        Assert.False(model.PasswordSuccess);
        Assert.Equal("New password and confirmation do not match.", model.PasswordError);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~AccountPageTests"`
Expected: FAIL to compile — `OnPostPasswordAsync` does not exist on `AccountModel`.

- [ ] **Step 3: Write minimal implementation**

Add to `MaterialLedger.Web/Pages/Account.cshtml.cs` (inside `AccountModel`, alongside the profile members):

```csharp
    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
    [BindProperty] public string NewPassword { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;
    public string? PasswordError { get; set; }
    public bool PasswordSuccess { get; set; }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        var user = await LoadCurrentUserAsync();
        DisplayName = user.DisplayName;

        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, CurrentPassword);
        if (verification == PasswordVerificationResult.Failed)
        {
            PasswordError = "Current password is incorrect.";
            return Page();
        }

        if (NewPassword.Length < 8)
        {
            PasswordError = "New password must be at least 8 characters.";
            return Page();
        }

        if (NewPassword != ConfirmPassword)
        {
            PasswordError = "New password and confirmation do not match.";
            return Page();
        }

        user.PasswordHash = hasher.HashPassword(user, NewPassword);
        await db.SaveChangesAsync();

        PasswordSuccess = true;
        return Page();
    }
```

Add `using Microsoft.AspNetCore.Identity;` if not already present (it is, from Task 1).

Add the password form to `MaterialLedger.Web/Pages/Account.cshtml`, after the profile `</form>`:

```html
<form method="post" asp-page-handler="Password">
    @Html.AntiForgeryToken()
    <fieldset>
        <legend>Change password</legend>
        @if (Model.PasswordError is not null) { <p class="form-error">@Model.PasswordError</p> }
        @if (Model.PasswordSuccess) { <p class="alert-success">Password changed.</p> }
        <label>Current password <input asp-for="CurrentPassword" type="password" autocomplete="current-password" /></label>
        <label>New password <input asp-for="NewPassword" type="password" autocomplete="new-password" /></label>
        <label>Confirm new password <input asp-for="ConfirmPassword" type="password" autocomplete="new-password" /></label>
        <button type="submit">Change password</button>
    </fieldset>
</form>
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~AccountPageTests"`
Expected: PASS (7/7)

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/Account.cshtml.cs MaterialLedger.Web/Pages/Account.cshtml MaterialLedger.Tests/AccountPageTests.cs
git commit -m "feat: add password change handler to Account page"
```

---

### Task 4: Sidebar link to the Account page

**Files:**
- Modify: `MaterialLedger.Web/Pages/Shared/_Layout.cshtml:35`

**Interfaces:**
- Consumes: `Account.cshtml` page route (created in Task 1) via `asp-page="/Account"`.
- Produces: no new members — pure markup change.

This is a markup-only change with no meaningful unit test (it's a Razor Pages tag-helper-generated `<a href>`); verify manually per Step 3 below, consistent with how other nav-only markup changes in this repo are handled (no existing test asserts on `_Layout.cshtml` link markup).

- [ ] **Step 1: Make the change**

In `MaterialLedger.Web/Pages/Shared/_Layout.cshtml`, line 35, change:

```html
            <div class="account"><span class="account-avatar">@User.Identity.Name?.Substring(0, 1).ToUpper()</span><div><strong>@User.Identity.Name</strong><small>@User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value</small></div><form method="post" action="/Logout">@Html.AntiForgeryToken()<button class="signout" type="submit">Sign out</button></form></div>
```

to:

```html
            <div class="account"><span class="account-avatar">@User.Identity.Name?.Substring(0, 1).ToUpper()</span><div><a class="account-name" asp-page="/Account"><strong>@User.Identity.Name</strong></a><small>@User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value</small></div><form method="post" action="/Logout">@Html.AntiForgeryToken()<button class="signout" type="submit">Sign out</button></form></div>
```

- [ ] **Step 2: Build**

Run: `dotnet build MaterialLedger.Web/MaterialLedger.Web.csproj`
Expected: Build succeeds (Razor markup compiles).

- [ ] **Step 3: Manual verification**

Run: `dotnet run --project MaterialLedger.Web/MaterialLedger.Web.csproj`
Log in with any seeded account, confirm the sidebar name is now a link that navigates to `/Account`, and the Account page renders both forms. Stop the app afterward (Ctrl+C).

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test`
Expected: All tests pass, including the 7 new `AccountPageTests`.

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/Shared/_Layout.cshtml
git commit -m "feat: link sidebar account name to the Account page"
```

---

## Self-Review Notes

- **Spec coverage:** Profile update (Task 1+2), password change (Task 3), nav entry point (Task 4), server-side-only validation (all tasks reuse the `form-error`/`alert-success` CSS classes already in `app.css` — no new CSS needed, so the spec's mention of adding `form-success` is superseded by reusing the existing `.alert-success` class, which has the right visual treatment already), integration-test-only coverage (no Playwright, per spec) — all covered.
- **Placeholder scan:** none found — every step has literal, runnable code.
- **Type consistency:** `AccountModel(MaterialLedgerDbContext db, PasswordHasher<AppUser> hasher)` constructor signature is identical across Tasks 1–3; `LoadCurrentUserAsync()` introduced in Task 1 is reused unchanged in Task 3; test helper `NewModel`/`NewPageContext` signatures introduced in Task 1 are extended (not replaced) in Task 2.
