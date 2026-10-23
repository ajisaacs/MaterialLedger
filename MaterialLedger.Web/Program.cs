using System.Text.Json.Serialization;
using MaterialLedger.Data;
using MaterialLedger.Web.Attachments;
using MaterialLedger.Web.Api;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Authentication;
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
builder.Services.AddScoped<MaterialSpecSeeder>();
builder.Services.AddScoped<MaterialLedger.Data.Idempotency.OperationLogGuard>();
builder.Services.AddScoped<MaterialLedger.Data.Locations.LocationSlotService>();
builder.Services.AddScoped<MaterialLedger.Data.Locations.LocationService>();
builder.Services.AddScoped<MaterialLedger.Data.Materials.MaterialSpecService>();
builder.Services.AddScoped<MaterialLedger.Data.Materials.PepMaterialMappingService>();
builder.Services.AddScoped<MaterialLedger.Data.Materials.OpenNestMaterialMappingService>();
builder.Services.AddScoped<MaterialLedger.Data.Vendors.VendorService>();
builder.Services.AddScoped<MaterialLedger.Data.Vendors.VendorQueryService>();
builder.Services.AddScoped<MaterialLedger.Data.Census.OpeningBalanceService>();
builder.Services.AddScoped<MaterialLedger.Data.Receiving.ReceiveMaterialService>();
builder.Services.AddScoped<MaterialLedger.Data.Receiving.IReceiveMaterialService>(sp =>
    sp.GetRequiredService<MaterialLedger.Data.Receiving.ReceiveMaterialService>());
builder.Services.AddScoped<MaterialLedger.Data.PurchaseOrders.PurchaseOrderService>();
builder.Services.AddScoped(sp =>
{
    var configuredPath = builder.Configuration["Storage:AttachmentsPath"];
    var attachmentsPath = string.IsNullOrWhiteSpace(configuredPath)
        ? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "attachments")
        : Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(builder.Environment.ContentRootPath, configuredPath);
    return new MaterialLedger.Data.Attachments.AttachmentStorageService(
        sp.GetRequiredService<MaterialLedger.Data.MaterialLedgerDbContext>(), attachmentsPath);
});
builder.Services.AddScoped(sp => new MaterialLedger.Data.Inventory.InventoryQueryService(
    sp.GetRequiredService<MaterialLedger.Data.MaterialLedgerDbContext>(),
    builder.Configuration.GetValue("ReservationFreshnessThresholdDays", 90)));
builder.Services.AddScoped(sp => new MaterialLedger.Data.Inventory.NestingStockQueryService(
    sp.GetRequiredService<MaterialLedger.Data.MaterialLedgerDbContext>(),
    builder.Configuration.GetValue("ReservationFreshnessThresholdDays", 90)));
builder.Services.AddScoped<WorkQueueQueryService>();
builder.Services.AddHttpClient<PepNestQueryService>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["PepApi:BaseUrl"] ?? "http://hermes.lan:8085/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<OpenNestClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["OpenNest:BaseUrl"] ?? "http://barge.lan:8091/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped(sp => new MaterialLedger.Data.Reservations.ReservationService(
    sp.GetRequiredService<MaterialLedger.Data.MaterialLedgerDbContext>(),
    sp.GetRequiredService<MaterialLedger.Data.Idempotency.OperationLogGuard>(),
    builder.Configuration.GetValue("ReservationFreshnessThresholdDays", 90)));
builder.Services.AddScoped<MaterialLedger.Data.Reservations.IReservationService>(sp =>
    sp.GetRequiredService<MaterialLedger.Data.Reservations.ReservationService>());
builder.Services.AddScoped(sp => new MaterialLedger.Data.Closeout.CloseoutService(
    sp.GetRequiredService<MaterialLedger.Data.MaterialLedgerDbContext>(),
    sp.GetRequiredService<MaterialLedger.Data.Idempotency.OperationLogGuard>()));
builder.Services.AddScoped<MaterialLedger.Data.Closeout.ICloseoutService>(sp =>
    sp.GetRequiredService<MaterialLedger.Data.Closeout.CloseoutService>());
builder.Services.AddScoped(sp => new MaterialLedger.Data.Exceptions.MaterialExceptionService(
    sp.GetRequiredService<MaterialLedger.Data.MaterialLedgerDbContext>(),
    sp.GetRequiredService<MaterialLedger.Data.Idempotency.OperationLogGuard>(),
    builder.Configuration.GetValue("ReservationFreshnessThresholdDays", 90),
    builder.Configuration.GetValue("CloseoutOverdueThresholdDays", 1)));

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
        ApiAuthenticationEvents.Configure(options.Events);
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection(ApiKeyOptions.SectionName));
builder.Services.AddAuthorization(options =>
{
    options.AddMaterialLedgerPolicies();
    // /api/stock answers only to an API key, never to a browser session.
    options.AddPolicy(ApiKeyAuthenticationHandler.PolicyName, p => p
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser());
});
builder.Services.AddProblemDetails();
builder.Services.Configure<AttachmentUploadOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.AddSingleton<AttachmentUploadPolicy>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    var maxAttachmentBytes = builder.Configuration.GetValue<long>("Storage:MaxAttachmentBytes", 10 * 1024 * 1024);
    options.MultipartBodyLengthLimit = AttachmentUploadLimits.MultipartBodyLengthLimit(maxAttachmentBytes);
});
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddRazorPages();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<AppUserSeeder>();
    await seeder.SeedAsync();

    var locationSeeder = scope.ServiceProvider.GetRequiredService<LocationSeeder>();
    await locationSeeder.SeedAsync();

    var materialSpecSeeder = scope.ServiceProvider.GetRequiredService<MaterialSpecSeeder>();
    await materialSpecSeeder.SeedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseStaticFiles();
app.UseRouting();
app.UseMiddleware<ApiExceptionHandler>();
app.UseApiProblemResponses();
app.UseAuthentication();
app.UseAuthorization();
app.MapMaterialLedgerApi(app.Environment);
app.MapRazorPages();
app.MapStockApi();
app.Run();

public partial class Program;
