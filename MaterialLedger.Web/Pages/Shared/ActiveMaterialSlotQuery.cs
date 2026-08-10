using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Shared;

public static class ActiveMaterialSlotQuery
{
    public static IQueryable<InventoryLocation> Create(MaterialLedgerDbContext db) => db.InventoryLocations
        .Where(x => x.IsActive && x.ParentInventoryLocationId != null
            && (x.LocationType == InventoryLocationType.Shelf
                || x.LocationType == InventoryLocationType.Bay
                || x.LocationType == InventoryLocationType.Staging))
        .Include(x => x.ParentInventoryLocation)
        .OrderBy(x => x.ParentInventoryLocation!.SortOrder)
        .ThenBy(x => x.SortOrder);
}
