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
