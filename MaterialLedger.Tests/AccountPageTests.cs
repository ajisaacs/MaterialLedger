using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Pages;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    private sealed class FakeAuthenticationService : Microsoft.AspNetCore.Authentication.IAuthenticationService
    {
        public ClaimsPrincipal? LastSignedInPrincipal { get; private set; }

        public Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            LastSignedInPrincipal = principal;
            return Task.CompletedTask;
        }
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

    [Fact]
    public async Task OnPostProfileAsync_NameTooLong_SetsErrorAndDoesNotSave()
    {
        await using var db = _fixture.CreateContext();
        var hasher = new PasswordHasher<AppUser>();
        var userId = await SeedUserAsync(db, hasher);
        var model = NewModel(db, hasher, userId);
        model.DisplayName = new string('a', 101);

        await model.OnPostProfileAsync();

        var reloaded = await db.AppUsers.AsNoTracking().SingleAsync(x => x.AppUserId == userId);
        Assert.Equal("Original Name", reloaded.DisplayName);
        Assert.False(model.ProfileSuccess);
        Assert.Equal("Display name must be 100 characters or fewer.", model.ProfileError);
    }

    [Fact]
    public async Task OnPostProfileAsync_ValidName_ReissuesAuthCookieWithNewName()
    {
        await using var db = _fixture.CreateContext();
        var hasher = new PasswordHasher<AppUser>();
        var userId = await SeedUserAsync(db, hasher);
        var model = NewModel(db, hasher, userId);
        model.DisplayName = "Renamed Person";

        await model.OnPostProfileAsync();

        var auth = (FakeAuthenticationService)model.HttpContext.RequestServices
            .GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationService>();
        Assert.NotNull(auth.LastSignedInPrincipal);
        Assert.Equal("Renamed Person", auth.LastSignedInPrincipal!.FindFirst(ClaimTypes.Name)?.Value);
        Assert.Equal(userId.ToString(), auth.LastSignedInPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal(AppUserRole.Operator.ToString(), auth.LastSignedInPrincipal.FindFirst(ClaimTypes.Role)?.Value);

        // The current request's principal must be updated too, not just the outgoing Set-Cookie header,
        // otherwise the page renders the old name on the response that shows the "updated" success message.
        Assert.Equal("Renamed Person", model.User.FindFirst(ClaimTypes.Name)?.Value);
    }

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
}
