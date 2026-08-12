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

public class CloseoutRemnantInput
{
    public decimal? MeasuredUsableWidthIn { get; set; }
    public decimal? MeasuredUsableLengthIn { get; set; }
    public long? DestinationLocationId { get; set; }
}

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
    [BindProperty] public List<CloseoutRemnantInput> Remnants { get; set; } = [new()];
    [BindProperty(SupportsGet = true)] public ReservationStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public ReservationEquipment? Equipment { get; set; }

    public MaterialReservation? Reservation { get; set; }
    public List<InventoryLocation> DestinationChoices { get; set; } = [];
    public string? Error { get; set; }
    public string WorkReference => Reservation is null ? string.Empty : ReservationWorkReference.Describe(Reservation);

    public async Task<IActionResult> OnGetAsync(long id)
    {
        await LoadPageAsync(id);
        if (Reservation is null)
            return NotFound();
        CorrelationId = Guid.NewGuid();
        PrefillExpectedRemnants();
        return Page();
    }

    public async Task<IActionResult> OnPostCloseoutAsync(long id)
    {
        if (Outcome is null)
            return await ReturnWithErrorAsync(id, "Select the physical cut result before recording closeout.");

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var recordedRemnants = Remnants
                .Where(x => x.MeasuredUsableWidthIn is not null || x.MeasuredUsableLengthIn is not null || x.DestinationLocationId is not null)
                .Select(x => new UsableDropRequest(x.MeasuredUsableWidthIn ?? 0, x.MeasuredUsableLengthIn ?? 0, x.DestinationLocationId ?? 0))
                .ToList();
            var request = new CloseoutConsumptionRequest(id, Convert.FromBase64String(ExpectedRowVersionBase64), Outcome.Value,
                MeasuredUsableWidthIn, MeasuredUsableLengthIn, DestinationLocationId, Notes,
                recordedRemnants.Count > 0 ? recordedRemnants : null);
            var result = await closeout.CloseoutConsumptionAsync(CorrelationId, request, userId);
            return RedirectToPage("Success", new { id, remnantCode = result.RemnantPublicItemCode, remnantItemId = result.RemnantInventoryItemId, Status, Equipment });
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

    private void PrefillExpectedRemnants()
    {
        var expectedRemnants = Reservation!.ExpectedRemnants
            .OrderBy(x => x.Sequence)
            .Select(x => new CloseoutRemnantInput
            {
                MeasuredUsableWidthIn = x.WidthIn,
                MeasuredUsableLengthIn = x.LengthIn
            })
            .ToList();

        if (expectedRemnants.Count == 0 && Reservation.ExpectedRemainderWidthIn is not null && Reservation.ExpectedRemainderLengthIn is not null)
        {
            expectedRemnants.Add(new CloseoutRemnantInput
            {
                MeasuredUsableWidthIn = Reservation.ExpectedRemainderWidthIn,
                MeasuredUsableLengthIn = Reservation.ExpectedRemainderLengthIn
            });
        }

        if (expectedRemnants.Count == 0)
            return;

        Outcome = ProductionConsumptionOutcome.UsableDropCreated;
        Remnants = expectedRemnants;
    }

    private async Task LoadPageAsync(long id)
    {
        Reservation = await db.MaterialReservations
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.InventoryLocation)
            .Include(x => x.ExpectedRemnants)
            .FirstOrDefaultAsync(x => x.MaterialReservationId == id);
        DestinationChoices = await db.InventoryLocations
            .Where(x => x.IsActive && DestinationChoiceTypes.Contains(x.LocationType))
            .OrderBy(x => x.LocationCode)
            .ToListAsync();
    }
}
