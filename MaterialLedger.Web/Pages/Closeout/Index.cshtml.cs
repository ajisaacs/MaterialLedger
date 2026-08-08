using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Closeout;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Closeout;

[Authorize(Policy = MaterialLedgerPolicies.RequireOperatorOrManager)]
public class IndexModel(MaterialLedgerDbContext db, CloseoutService closeout) : PageModel
{
    public List<MaterialReservation> Reservations { get; set; } = [];
    public Dictionary<long, Guid> CorrelationIdsByReservationId { get; set; } = [];
    public string? Error { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostStartJobAsync(long id, Guid correlationId, string expectedRowVersionBase64)
    {
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await closeout.MarkReservationAwaitingCloseoutAsync(correlationId, id, Convert.FromBase64String(expectedRowVersionBase64), userId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ReservationConcurrencyException or OperationConflictException or DbUpdateException)
        {
            Error = ex is DbUpdateException ? "This reservation was changed by someone else — refresh and try again." : ex.Message;
            await LoadAsync();
            return Page();
        }

        return RedirectToPage("Index");
    }

    private async Task LoadAsync()
    {
        Reservations = await db.MaterialReservations
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Where(x => x.Status == ReservationStatus.Approved || x.Status == ReservationStatus.AwaitingCloseout)
            .OrderByDescending(x => x.ApprovedAtUtc)
            .ToListAsync();
        CorrelationIdsByReservationId = Reservations.ToDictionary(x => x.MaterialReservationId, _ => Guid.NewGuid());
    }
}
