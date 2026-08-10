using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Locations;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Locations;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class CreateModel(MaterialLedgerDbContext db, LocationService locationService) : PageModel
{
    [BindProperty] public string LocationCode { get; set; } = string.Empty;
    [BindProperty] public string DisplayName { get; set; } = string.Empty;
    [BindProperty] public InventoryLocationType LocationType { get; set; } = InventoryLocationType.Area;
    [BindProperty] public InventoryStorageMode StorageMode { get; set; } = InventoryStorageMode.Other;
    [BindProperty] public long? ParentInventoryLocationId { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }

    public List<InventoryLocation> AvailableAreas { get; set; } = [];
    public string? Error { get; set; }

    public async Task OnGetAsync()
    {
        AvailableAreas = await db.InventoryLocations
            .Where(x => x.LocationType == InventoryLocationType.Area && x.IsActive)
            .OrderBy(x => x.DisplayName)
            .ToListAsync();
        CorrelationId = Guid.NewGuid();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        AvailableAreas = await db.InventoryLocations
            .Where(x => x.LocationType == InventoryLocationType.Area && x.IsActive)
            .OrderBy(x => x.DisplayName)
            .ToListAsync();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new CreateLocationRequest(LocationCode, DisplayName, LocationType, StorageMode, ParentInventoryLocationId);
            var created = await locationService.CreateLocationAsync(CorrelationId, request, userId);

            return created.LocationType == InventoryLocationType.Rack
                ? RedirectToPage("Details", new { code = created.LocationCode })
                : RedirectToPage("Index");
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationConflictException)
        {
            Error = ex is OperationConflictException
                ? "This form was already submitted with different values — please review and resubmit."
                : ex.Message;
            return Page();
        }
    }
}
