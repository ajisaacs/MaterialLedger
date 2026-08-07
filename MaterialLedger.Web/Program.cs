using MaterialLedger.Data;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MaterialLedgerDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("MaterialLedger")));

builder.Services.AddSingleton<PasswordHasher<MaterialLedger.Data.Models.AppUser>>();
builder.Services.AddScoped<AppUserAuthenticator>();
builder.Services.AddScoped<AppUserSeeder>();
builder.Services.AddScoped<LocationSeeder>();
builder.Services.AddScoped<MaterialLedger.Data.Idempotency.OperationLogGuard>();
builder.Services.AddScoped<MaterialLedger.Data.Locations.LocationSlotService>();
builder.Services.AddScoped<MaterialLedger.Data.Materials.MaterialSpecService>();
builder.Services.AddScoped<MaterialLedger.Data.Census.OpeningBalanceService>();

// Sessions are configured to never expire on a timer (see ExpireTimeSpan below), but the auth
// cookie's contents are encrypted/signed with ASP.NET Core Data Protection keys. Without explicit
// key persistence, keys are generated in memory (or an ephemeral profile directory under a service
// account/container) and are lost on every restart, silently invalidating every "long-lived" session.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeyPath"] ?? "keys"))
    .SetApplicationName("MaterialLedger");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.AccessDeniedPath = "/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromDays(3650); // "sessions do not expire on a timer"
        options.SlidingExpiration = true;
        options.Cookie.IsEssential = true;
    });
builder.Services.AddAuthorization(options => options.AddMaterialLedgerPolicies());
builder.Services.AddRazorPages();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<AppUserSeeder>();
    await seeder.SeedAsync();

    var locationSeeder = scope.ServiceProvider.GetRequiredService<LocationSeeder>();
    await locationSeeder.SeedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.Run();
