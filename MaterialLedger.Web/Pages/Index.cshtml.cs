using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages;

[Authorize]
public class IndexModel(MaterialLedgerDbContext db) : PageModel
{
    public DashboardSummary Summary { get; private set; } = new(0, 0, 0, 0);

    public async Task OnGetAsync()
    {
        var activeReservationStatuses = new[]
        {
            ReservationStatus.Proposed,
            ReservationStatus.Approved,
            ReservationStatus.AwaitingCloseout
        };

        Summary = new DashboardSummary(
            await db.InventoryItems.CountAsync(item => item.Status == InventoryItemStatus.Available && item.QuantityOnHand > 0),
            await db.MaterialReservations.CountAsync(reservation => activeReservationStatuses.Contains(reservation.Status)),
            await db.MaterialReservations.CountAsync(reservation => reservation.Status == ReservationStatus.AwaitingCloseout),
            await db.MaterialExceptions.CountAsync(exception => exception.Status == MaterialExceptionStatus.Open));
    }

    public sealed record DashboardSummary(int AvailableMaterial, int ActiveReservations, int AwaitingCloseout, int AttentionNeeded);
}
