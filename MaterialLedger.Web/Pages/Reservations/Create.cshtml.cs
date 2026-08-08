using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Reservations;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class CreateModel(MaterialLedgerDbContext db, ReservationService reservations) : PageModel
{
    [BindProperty] public long InventoryItemId { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public string? ShopOpsJobId { get; set; }
    [BindProperty] public string? DrawingId { get; set; }
    [BindProperty] public string? DrawingRevision { get; set; }
    [BindProperty] public ReservationEquipment? Equipment { get; set; }
    [BindProperty] public int? PepNestYear { get; set; }
    [BindProperty] public string? PepNestName { get; set; }
    [BindProperty] public string? LaserProgramRecordId { get; set; }
    [BindProperty] public bool HasManualEstimate { get; set; }
    [BindProperty] public decimal? ExpectedRemainderWidthIn { get; set; }
    [BindProperty] public decimal? ExpectedRemainderLengthIn { get; set; }

    public InventoryItem? Item { get; set; }
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long inventoryItemId)
    {
        Item = await db.InventoryItems.Include(x => x.MaterialSpec).Include(x => x.InventoryLocation)
            .FirstOrDefaultAsync(x => x.InventoryItemId == inventoryItemId);
        if (Item is null)
            return NotFound();
        InventoryItemId = inventoryItemId;
        CorrelationId = Guid.NewGuid();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Item = await db.InventoryItems.Include(x => x.MaterialSpec).Include(x => x.InventoryLocation)
            .FirstOrDefaultAsync(x => x.InventoryItemId == InventoryItemId);
        if (Item is null)
            return NotFound();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new CreateReservationRequest(
                InventoryItemId, ShopOpsJobId, DrawingId, DrawingRevision, Equipment,
                PepNestYear, PepNestName, LaserProgramRecordId,
                HasManualEstimate ? ExpectedRemainderWidthIn : null,
                HasManualEstimate ? ExpectedRemainderLengthIn : null,
                HasManualEstimate ? ExpectedRemainderSource.Manual : null);

            var result = await reservations.CreateReservationAsync(CorrelationId, request, userId);
            return RedirectToPage("Details", new { id = result.MaterialReservationId });
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationConflictException)
        {
            Error = ex.Message;
            return Page();
        }
    }
}
