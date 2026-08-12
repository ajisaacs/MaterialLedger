using MaterialLedger.Data;
using MaterialLedger.Data.Closeout;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Closeout;

[Authorize(Policy = MaterialLedgerPolicies.RequireOperatorOrManager)]
public class IndexModel(MaterialLedgerDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public ReservationStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public ReservationEquipment? Equipment { get; set; }
    public List<MaterialReservation> Reservations { get; set; } = [];

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public string WorkReference(MaterialReservation reservation) => ReservationWorkReference.Describe(reservation);

    public string UsableSize(MaterialReservation reservation)
    {
        var item = reservation.InventoryItem;
        return item?.UsableWidthIn is not null && item.UsableLengthIn is not null
            ? $"Usable {item.UsableWidthIn:0.###} × {item.UsableLengthIn:0.###} in"
            : "Usable size not recorded";
    }


    private async Task LoadAsync()
    {
        var query = db.MaterialReservations
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.InventoryLocation)
            .Where(x => x.Status == ReservationStatus.Approved || x.Status == ReservationStatus.AwaitingCloseout);

        if (Status is not null) query = query.Where(x => x.Status == Status);
        if (Equipment is not null) query = query.Where(x => x.Equipment == Equipment);

        Reservations = await query
            .OrderByDescending(x => x.ApprovedAtUtc)
            .ThenByDescending(x => x.MaterialReservationId)
            .ToListAsync();
    }
}
