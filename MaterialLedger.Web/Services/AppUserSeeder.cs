using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Services;

public record SeedAccountConfig(string Username, string DisplayName, string Role, string InitialPassword);

public class AppUserSeeder(MaterialLedgerDbContext db, PasswordHasher<AppUser> hasher, IConfiguration configuration)
{
    public async Task SeedAsync()
    {
        var seedAccounts = configuration.GetSection("SeedAccounts").Get<List<SeedAccountConfig>>() ?? [];

        foreach (var seed in seedAccounts)
        {
            if (string.IsNullOrWhiteSpace(seed.InitialPassword))
                continue; // not yet provisioned with a real password — skip rather than seed a useless account

            var exists = await db.AppUsers.AnyAsync(x => x.Username == seed.Username);
            if (exists)
                continue;

            var user = new AppUser
            {
                Username = seed.Username,
                DisplayName = seed.DisplayName,
                Role = Enum.Parse<AppUserRole>(seed.Role),
                IsActive = true
            };
            user.PasswordHash = hasher.HashPassword(user, seed.InitialPassword);
            db.AppUsers.Add(user);
        }

        await db.SaveChangesAsync();
    }
}
