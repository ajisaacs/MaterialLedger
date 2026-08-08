// MaterialLedger.Web/Pages/Reservations/Index.cshtml.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Reservations;

[Authorize]
public class IndexModel(MaterialLedgerDbContext db) : PageModel
{
    public List<MaterialReservation> Reservations { get; set; } = [];

    public async Task OnGetAsync()
    {
        Reservations = await db.MaterialReservations
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Where(x => x.Status == ReservationStatus.Proposed
                || x.Status == ReservationStatus.Approved
                || x.Status == ReservationStatus.AwaitingCloseout)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync();
    }
}
