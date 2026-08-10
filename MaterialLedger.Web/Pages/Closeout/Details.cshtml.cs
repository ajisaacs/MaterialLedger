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
public class DetailsModel(MaterialLedgerDbContext db, ICloseoutService closeout) : PageModel
{
    private static readonly InventoryLocationType[] DestinationChoiceTypes =
    [
        InventoryLocationType.Shelf, InventoryLocationType.Bay, InventoryLocationType.Staging
    ];

    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public string ExpectedRowVersionBase64 { get; set; } = string.Empty;
    [BindProperty] public ProductionConsumptionOutcome? Outcome { get; set; }
    [BindProperty] public string? Notes { get; set; }
    [BindProperty] public decimal? MeasuredUsableWidthIn { get; set; }
    [BindProperty] public decimal? MeasuredUsableLengthIn { get; set; }
    [BindProperty] public long? DestinationLocationId { get; set; }
    [BindProperty(SupportsGet = true)] public ReservationStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public ReservationEquipment? Equipment { get; set; }

    public MaterialReservation? Reservation { get; set; }
    public List<InventoryLocation> DestinationChoices { get; set; } = [];
    public string? Error { get; set; }
    public string WorkReference => Reservation is null ? string.Empty : DescribeWorkReference(Reservation);

    public async Task<IActionResult> OnGetAsync(long id)
    {
        await LoadPageAsync(id);
        if (Reservation is null)
            return NotFound();
        CorrelationId = Guid.NewGuid();
        return Page();
    }

    public async Task<IActionResult> OnPostCloseoutAsync(long id)
    {
        if (Outcome is null)
            return await ReturnWithErrorAsync(id, "Select the physical cut result before recording closeout.");

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new CloseoutConsumptionRequest(id, Convert.FromBase64String(ExpectedRowVersionBase64), Outcome.Value,
                MeasuredUsableWidthIn, MeasuredUsableLengthIn, DestinationLocationId, Notes);
            var result = await closeout.CloseoutConsumptionAsync(CorrelationId, request, userId);
            return RedirectToPage("Success", new { id, remnantCode = result.RemnantPublicItemCode, Status, Equipment });
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException
            or ReservationConcurrencyException or OperationConflictException or DbUpdateException)
        {
            return await ReturnWithErrorAsync(id,
                ex is DbUpdateException ? "This reservation was changed by someone else — refresh and try again." : ex.Message);
        }
    }

    private async Task<IActionResult> ReturnWithErrorAsync(long id, string error)
    {
        await LoadPageAsync(id);
        if (Reservation is null)
            return NotFound();

        CorrelationId = Guid.NewGuid();
        Error = error;
        return Page();
    }

    private async Task LoadPageAsync(long id)
    {
        Reservation = await db.MaterialReservations
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.InventoryLocation)
            .FirstOrDefaultAsync(x => x.MaterialReservationId == id);
        DestinationChoices = await db.InventoryLocations
            .Where(x => x.IsActive && DestinationChoiceTypes.Contains(x.LocationType))
            .OrderBy(x => x.LocationCode)
            .ToListAsync();
    }

    private static string DescribeWorkReference(MaterialReservation reservation)
    {
        if (!string.IsNullOrWhiteSpace(reservation.ShopOpsJobId))
            return reservation.ShopOpsJobId;
        if (!string.IsNullOrWhiteSpace(reservation.DrawingId))
            return string.IsNullOrWhiteSpace(reservation.DrawingRevision)
                ? reservation.DrawingId
                : $"{reservation.DrawingId} rev {reservation.DrawingRevision}";
        if (!string.IsNullOrWhiteSpace(reservation.PepNestName))
            return reservation.PepNestYear is null ? reservation.PepNestName : $"{reservation.PepNestYear} {reservation.PepNestName}";
        return "No work reference recorded";
    }
}
