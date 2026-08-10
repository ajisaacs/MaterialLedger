// MaterialLedger.Web/Pages/Reservations/Details.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Reservations;

[Authorize]
public class DetailsModel(MaterialLedgerDbContext db, IReservationService reservations) : PageModel
{
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public string ExpectedRowVersionBase64 { get; set; } = string.Empty;
    [BindProperty] public string? ReleaseNotes { get; set; }
    [BindProperty(SupportsGet = true)] public ReservationStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public ReservationEquipment? Equipment { get; set; }

    public MaterialReservation? Reservation { get; set; }
    public bool CanAct { get; set; }
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        Reservation = await LoadAsync(id);
        if (Reservation is null)
            return NotFound();
        CanAct = User.IsInRole("Programmer") || User.IsInRole("Manager");
        CorrelationId = Guid.NewGuid();
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(long id)
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager"))
            return Forbid();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await reservations.ApproveReservationAsync(CorrelationId, id, Convert.FromBase64String(ExpectedRowVersionBase64), userId);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ReservationBalanceException
            or ReservationConcurrencyException or OperationConflictException or DbUpdateException)
        {
            var message = ex is DbUpdateException
                ? "This reservation was changed by someone else — refresh and try again."
                : ex.Message;
            return await ShowErrorAsync(id, message);
        }

        return RedirectToPage("Details", new { id, Status, Equipment });
    }

    public async Task<IActionResult> OnPostReleaseAsync(long id)
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager"))
            return Forbid();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await reservations.ReleaseReservationAsync(CorrelationId, id, Convert.FromBase64String(ExpectedRowVersionBase64), userId, ReleaseNotes);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ReservationConcurrencyException or OperationConflictException or DbUpdateException)
        {
            var message = ex is DbUpdateException
                ? "This reservation was changed by someone else — refresh and try again."
                : ex.Message;
            return await ShowErrorAsync(id, message);
        }

        return RedirectToPage("Details", new { id, Status, Equipment });
    }

    private async Task<IActionResult> ShowErrorAsync(long id, string message)
    {
        Reservation = await LoadAsync(id);
        CanAct = true;
        CorrelationId = Guid.NewGuid();
        Error = message;
        return Page();
    }

    private Task<MaterialReservation?> LoadAsync(long id) =>
        db.MaterialReservations
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.InventoryLocation)
            .Include(x => x.ExpectedRemnants)
            .Include(x => x.CreatedByUser)
            .Include(x => x.ApprovedByUser)
            .FirstOrDefaultAsync(x => x.MaterialReservationId == id);
}
