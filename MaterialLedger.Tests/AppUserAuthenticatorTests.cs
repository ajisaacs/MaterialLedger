using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

public class AppUserAuthenticatorTests
{
    private static MaterialLedgerDbContext InMemoryContext()
    {
        var options = new DbContextOptionsBuilder<MaterialLedgerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MaterialLedgerDbContext(options);
    }

    [Fact]
    public async Task VerifyAsync_CorrectPassword_ReturnsUser()
    {
        await using var db = InMemoryContext();
        var hasher = new PasswordHasher<AppUser>();
        var user = new AppUser
        {
            Username = "jdoe", DisplayName = "J Doe", Role = AppUserRole.Operator, IsActive = true
        };
        user.PasswordHash = hasher.HashPassword(user, "correct-horse-battery-staple");
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();

        var authenticator = new AppUserAuthenticator(db, hasher);
        var result = await authenticator.VerifyAsync("jdoe", "correct-horse-battery-staple");

        Assert.NotNull(result);
        Assert.Equal("jdoe", result!.Username);
    }

    [Fact]
    public async Task VerifyAsync_WrongPassword_ReturnsNull()
    {
        await using var db = InMemoryContext();
        var hasher = new PasswordHasher<AppUser>();
        var user = new AppUser
        {
            Username = "jdoe", DisplayName = "J Doe", Role = AppUserRole.Operator, IsActive = true
        };
        user.PasswordHash = hasher.HashPassword(user, "correct-horse-battery-staple");
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();

        var authenticator = new AppUserAuthenticator(db, hasher);
        var result = await authenticator.VerifyAsync("jdoe", "wrong-password");

        Assert.Null(result);
    }

    [Fact]
    public async Task VerifyAsync_InactiveUser_ReturnsNullEvenWithCorrectPassword()
    {
        await using var db = InMemoryContext();
        var hasher = new PasswordHasher<AppUser>();
        var user = new AppUser
        {
            Username = "jdoe", DisplayName = "J Doe", Role = AppUserRole.Operator, IsActive = false
        };
        user.PasswordHash = hasher.HashPassword(user, "correct-horse-battery-staple");
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();

        var authenticator = new AppUserAuthenticator(db, hasher);
        var result = await authenticator.VerifyAsync("jdoe", "correct-horse-battery-staple");

        Assert.Null(result);
    }
}
