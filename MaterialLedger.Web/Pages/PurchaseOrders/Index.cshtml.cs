// MaterialLedger.Web/Pages/PurchaseOrders/Index.cshtml.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.PurchaseOrders;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class IndexModel(MaterialLedgerDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string StatusFilter { get; set; } = "Open";
    public List<PurchaseOrderRow> Orders { get; set; } = [];

    public async Task OnGetAsync()
    {
        var orders = await db.PurchaseOrders
            .Include(x => x.Lines)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync();

        Orders = orders
            .Select(order => new PurchaseOrderRow(order, PurchaseOrderStatusDisplay.DeriveStatus(order.Lines)))
            .Where(row => StatusFilter == "All" || row.Status.ToString() == StatusFilter)
            .ToList();
    }
}

public sealed record PurchaseOrderRow(PurchaseOrder Order, PurchaseOrderStatus Status);
