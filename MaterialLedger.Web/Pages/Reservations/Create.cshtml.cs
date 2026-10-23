using System.Security.Claims;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Reservations;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class CreateModel(InventoryQueryService inventoryQuery, ReservationService reservations) : PageModel
{
    [BindProperty] public long InventoryItemId { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public string? ShopOpsJobId { get; set; }
    [BindProperty] public string? DrawingId { get; set; }
    [BindProperty] public string? DrawingRevision { get; set; }
    [BindProperty] public ReservationEquipment? Equipment { get; set; }
    [BindProperty] public int? PepNestYear { get; set; }
    [BindProperty] public string? PepNestName { get; set; }
    [BindProperty] public Guid? OpenNestNestId { get; set; }
    [BindProperty] public string? NestName { get; set; }
    [BindProperty] public string? LaserProgramRecordId { get; set; }
    [BindProperty] public List<string?> ExpectedDropSizes { get; set; } = [string.Empty];

    public InventoryItemSummary? Item { get; set; }
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long inventoryItemId, int? pepNestYear = null, string? pepNestName = null, int? pepMaterialNumber = null,
        Guid? openNestNestId = null, string? nestName = null)
    {
        Item = await inventoryQuery.GetItemSummaryAsync(inventoryItemId);
        if (Item is null)
        {
            TempData["Message"] = "Select an item from Inventory Search to create a reservation.";
            return RedirectToPage("/Inventory/Search");
        }
        InventoryItemId = inventoryItemId;
        PepNestYear = pepNestYear;
        PepNestName = pepNestName;
        OpenNestNestId = openNestNestId;
        NestName = nestName;
        CorrelationId = Guid.NewGuid();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Item = await inventoryQuery.GetItemSummaryAsync(InventoryItemId);
        if (Item is null)
            return NotFound();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var expectedRemnants = new List<ExpectedRemnantRequest>();
            for (var index = 0; index < ExpectedDropSizes.Count; index++)
            {
                var dropSize = ExpectedDropSizes[index];
                if (string.IsNullOrWhiteSpace(dropSize))
                    continue;

                if (!ExpectedDropSizeParser.TryParse(dropSize, out var widthIn, out var lengthIn))
                    throw new InvalidOperationException($"Expected drop {index + 1} must be width x length using positive inch values (for example, 12.5x24 or 12 1/2 x 24 3/8).");

                expectedRemnants.Add(new ExpectedRemnantRequest(widthIn, lengthIn, ExpectedRemainderSource.Manual));
            }

            var request = new CreateReservationRequest(
                InventoryItemId, ShopOpsJobId, DrawingId, DrawingRevision, Equipment,
                PepNestYear, PepNestName, LaserProgramRecordId,
                null, null, null,
                expectedRemnants.Count > 0 ? expectedRemnants : null,
                OpenNestNestId: OpenNestNestId, NestName: NestName);

            var result = await reservations.CreateReservationAsync(CorrelationId, request, userId);
            return RedirectToPage("Details", new { id = result.Result.MaterialReservationId });
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationConflictException)
        {
            Error = ex.Message;
            return Page();
        }
    }
}
