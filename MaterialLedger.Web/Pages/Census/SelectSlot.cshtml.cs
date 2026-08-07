using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Census;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class SelectSlotModel(MaterialLedgerDbContext db) : PageModel
{
    public List<InventoryLocation> ConfirmedSlots { get; set; } = [];

    public async Task OnGetAsync()
    {
        ConfirmedSlots = await db.InventoryLocations
            .Where(x => x.IsActive && x.ParentInventoryLocationId != null
                && (x.LocationType == InventoryLocationType.Shelf
                    || x.LocationType == InventoryLocationType.Bay
                    || x.LocationType == InventoryLocationType.Staging))
            .Include(x => x.ParentInventoryLocation)
            .OrderBy(x => x.ParentInventoryLocation!.SortOrder).ThenBy(x => x.SortOrder)
            .ToListAsync();
    }
}
