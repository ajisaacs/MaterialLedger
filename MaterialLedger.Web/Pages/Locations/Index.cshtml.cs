using MaterialLedger.Data;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Locations;

[Authorize]
public class IndexModel(MaterialLedgerDbContext db, InventoryQueryService inventoryQuery) : PageModel
{
    public List<InventoryLocation> Racks { get; set; } = [];
    public Dictionary<string, LocationSummary> Summaries { get; set; } = [];

    public async Task OnGetAsync()
    {
        Racks = await db.InventoryLocations
            .Where(x => x.LocationType == InventoryLocationType.Rack && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToListAsync();
        Summaries = (await inventoryQuery.GetRackSummariesAsync()).ToDictionary(x => x.LocationCode);
    }
}
