using MaterialLedger.Data;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Locations;

[Authorize]
public class DetailsModel(MaterialLedgerDbContext db, InventoryQueryService inventoryQuery) : PageModel
{
    public InventoryLocation? Rack { get; set; }
    public List<InventoryLocation> Slots { get; set; } = [];
    public ILookup<string, InventoryItemSummary> ItemsBySlotCode { get; set; } = Enumerable.Empty<InventoryItemSummary>().ToLookup(x => x.LocationCode);
    public bool CanConfirmSlots { get; set; }

    public async Task<IActionResult> OnGetAsync(string code)
    {
        Rack = await db.InventoryLocations.FirstOrDefaultAsync(x => x.LocationCode == code);
        if (Rack is null)
            return NotFound();

        Slots = await db.InventoryLocations
            .Where(x => x.ParentInventoryLocationId == Rack.InventoryLocationId && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToListAsync();
        var items = await inventoryQuery.GetItemsAtLocationsAsync(Slots.Select(x => x.InventoryLocationId).ToList());
        ItemsBySlotCode = items.ToLookup(x => x.LocationCode);
        CanConfirmSlots = !Rack.CapacityConfirmed && (User.IsInRole("Programmer") || User.IsInRole("Manager"));
        return Page();
    }
}
