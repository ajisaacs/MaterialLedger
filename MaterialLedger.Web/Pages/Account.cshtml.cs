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
