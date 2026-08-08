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
public class DetailsModel(MaterialLedgerDbContext db, CloseoutService closeout) : PageModel
{
    private static readonly InventoryLocationType[] DestinationChoiceTypes =
    [
        InventoryLocationType.Shelf, InventoryLocationType.Bay, InventoryLocationType.Staging
    ];

    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public string ExpectedRowVersionBase64 { get; set; } = string.Empty;
    [BindProperty] public string? Notes { get; set; }
    [BindProperty] public decimal? MeasuredUsableWidthIn { get; set; }
    [BindProperty] public decimal? MeasuredUsableLengthIn { get; set; }
    [BindProperty] public long? DestinationLocationId { get; set; }

    public MaterialReservation? Reservation { get; set; }
    public List<InventoryLocation> DestinationChoices { get; set; } = [];
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        Reservation = await LoadAsync(id);
        if (Reservation is null)
            return NotFound();
        DestinationChoices = await db.InventoryLocations
            .Where(x => x.IsActive && DestinationChoiceTypes.Contains(x.LocationType))
            .OrderBy(x => x.LocationCode)
            .ToListAsync();
        CorrelationId = Guid.NewGuid();
        return Page();
    }

    public Task<IActionResult> OnPostAllConsumedAsync(long id) =>
        SubmitAsync(id, ProductionConsumptionOutcome.AllConsumed);

    public Task<IActionResult> OnPostFullSheetReturnedAsync(long id) =>
        SubmitAsync(id, ProductionConsumptionOutcome.FullSheetReturned);

    public Task<IActionResult> OnPostUsableDropCreatedAsync(long id) =>
        SubmitAsync(id, ProductionConsumptionOutcome.UsableDropCreated);

    public Task<IActionResult> OnPostNeedsReviewAsync(long id) =>
        SubmitAsync(id, ProductionConsumptionOutcome.NeedsReview);

    private async Task<IActionResult> SubmitAsync(long id, ProductionConsumptionOutcome outcome)
    {
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new CloseoutConsumptionRequest(id, Convert.FromBase64String(ExpectedRowVersionBase64), outcome,
                MeasuredUsableWidthIn, MeasuredUsableLengthIn, DestinationLocationId, Notes);
            var result = await closeout.CloseoutConsumptionAsync(CorrelationId, request, userId);
            return RedirectToPage("Success", new { id, remnantCode = result.RemnantPublicItemCode });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or ReservationConcurrencyException or OperationConflictException or DbUpdateException)
        {
            Reservation = await LoadAsync(id);
            DestinationChoices = await db.InventoryLocations
                .Where(x => x.IsActive && DestinationChoiceTypes.Contains(x.LocationType))
                .OrderBy(x => x.LocationCode)
                .ToListAsync();
            CorrelationId = Guid.NewGuid();
            Error = ex is DbUpdateException ? "This reservation was changed by someone else — refresh and try again." : ex.Message;
            return Page();
        }
    }

    private Task<MaterialReservation?> LoadAsync(long id) =>
        db.MaterialReservations
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.InventoryLocation)
            .FirstOrDefaultAsync(x => x.MaterialReservationId == id);
}
