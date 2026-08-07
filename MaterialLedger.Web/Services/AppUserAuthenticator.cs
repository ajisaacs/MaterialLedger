using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Services;

public class AppUserAuthenticator(MaterialLedgerDbContext db, PasswordHasher<AppUser> hasher)
{
    public async Task<AppUser?> VerifyAsync(string username, string password)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(x => x.Username == username && x.IsActive);
        if (user is null)
            return null;

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : user;
    }
}
