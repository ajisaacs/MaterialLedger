using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Pages.Shared;
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
        ConfirmedSlots = await ActiveMaterialSlotQuery.Create(db).ToListAsync();
    }
}
