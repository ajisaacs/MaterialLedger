using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
    [BindProperty] public string NewPassword { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;
    public string? PasswordError { get; set; }
    public bool PasswordSuccess { get; set; }

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

        if (trimmed.Length > 100)
        {
            ProfileError = "Display name must be 100 characters or fewer.";
            DisplayName = user.DisplayName;
            return Page();
        }

        user.DisplayName = trimmed;
        await db.SaveChangesAsync();

        var principal = AppUserPrincipalFactory.Create(user);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = true });
        HttpContext.User = principal;

        DisplayName = trimmed;
        ProfileSuccess = true;
        return Page();
    }

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

    private async Task<AppUser> LoadCurrentUserAsync()
    {
        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        return await db.AppUsers.SingleAsync(x => x.AppUserId == userId);
    }
}
