using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Closeout;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Closeout;

[Authorize(Policy = MaterialLedgerPolicies.RequireOperatorOrManager)]
public class IndexModel(MaterialLedgerDbContext db, ICloseoutService closeout) : PageModel
{
    [BindProperty(SupportsGet = true)] public ReservationStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public ReservationEquipment? Equipment { get; set; }
    public List<MaterialReservation> Reservations { get; set; } = [];
    public Dictionary<long, Guid> CorrelationIdsByReservationId { get; set; } = [];
    public string? Error { get; set; }

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

    public string PendingFor(MaterialReservation reservation)
    {
        var startedAt = reservation.ApprovedAtUtc ?? reservation.CreatedAtUtc;
        var elapsed = DateTime.UtcNow - startedAt;
        return elapsed.TotalDays >= 1
            ? $"{Math.Floor(elapsed.TotalDays):0} day(s)"
            : elapsed.TotalHours >= 1
                ? $"{Math.Floor(elapsed.TotalHours):0} hour(s)"
                : "Less than 1 hour";
    }

    public async Task<IActionResult> OnPostStartJobAsync(long id, Guid correlationId, string expectedRowVersionBase64)
    {
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await closeout.MarkReservationAwaitingCloseoutAsync(correlationId, id, Convert.FromBase64String(expectedRowVersionBase64), userId);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ReservationConcurrencyException or OperationConflictException or DbUpdateException)
        {
            Error = ex is DbUpdateException ? "This reservation was changed by someone else — refresh and try again." : ex.Message;
            await LoadAsync();
            return Page();
        }

        return RedirectToPage("Index", new { Status, Equipment });
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
        CorrelationIdsByReservationId = Reservations.ToDictionary(x => x.MaterialReservationId, _ => Guid.NewGuid());
    }
}
