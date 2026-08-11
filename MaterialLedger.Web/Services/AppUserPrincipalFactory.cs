using System.Security.Claims;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MaterialLedger.Web.Services;

public static class AppUserPrincipalFactory
{
    public static ClaimsPrincipal Create(AppUser user) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.AppUserId.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        ],
        CookieAuthenticationDefaults.AuthenticationScheme));
}
