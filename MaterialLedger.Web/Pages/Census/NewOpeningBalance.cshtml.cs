using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Census;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Census;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class NewOpeningBalanceModel(MaterialLedgerDbContext db, OpeningBalanceService openingBalance) : PageModel
{
    [BindProperty] public long LocationId { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public long? ExistingMaterialSpecId { get; set; }
    [BindProperty] public bool IsDrop { get; set; }
    [BindProperty] public decimal Quantity { get; set; }
    [BindProperty] public decimal? UsableWidthIn { get; set; }
    [BindProperty] public decimal? UsableLengthIn { get; set; }
    [BindProperty] public ShapeClassification ShapeClassification { get; set; } = ShapeClassification.Rectangular;
    [BindProperty] public bool IsQuarantined { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public InventoryLocation? Location { get; set; }
    public List<InventoryItem> ExistingActiveItems { get; set; } = [];
    public List<MaterialSpec> AvailableSpecs { get; set; } = [];
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long locationId, long? copyFromItemId)
    {
        Location = await db.InventoryLocations.FindAsync(locationId);
        if (Location is null) return NotFound();

        LocationId = locationId;
        CorrelationId = Guid.NewGuid();
        ExistingActiveItems = (await openingBalance.GetActiveItemsAtLocationAsync(locationId)).ToList();
        AvailableSpecs = await LoadAvailableSpecsAsync();

        if (copyFromItemId is not null)
        {
            var prior = ExistingActiveItems.FirstOrDefault(x => x.InventoryItemId == copyFromItemId);
            if (prior is not null)
            {
                ExistingMaterialSpecId = prior.MaterialSpecId;
                IsDrop = prior.ItemKind == InventoryItemKind.Drop;
                UsableWidthIn = prior.UsableWidthIn;
                UsableLengthIn = prior.UsableLengthIn;
                ShapeClassification = prior.ShapeClassification;
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Location = await db.InventoryLocations.FindAsync(LocationId);
        ExistingActiveItems = (await openingBalance.GetActiveItemsAtLocationAsync(LocationId)).ToList();
        AvailableSpecs = await LoadAvailableSpecsAsync();

        if (ExistingMaterialSpecId is null)
        {
            Error = "Choose a material and thickness from the catalog.";
            return Page();
        }

        if (AvailableSpecs.All(s => s.MaterialSpecId != ExistingMaterialSpecId))
        {
            Error = "The selected material is no longer available. Please choose another.";
            return Page();
        }

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var itemKind = IsDrop ? InventoryItemKind.Drop
                : Quantity > 1 ? InventoryItemKind.FullSheetBundle
                : InventoryItemKind.FullSheet;
            var quantity = IsDrop ? 1m : Quantity;
            var request = new OpeningBalanceRequest(LocationId, ExistingMaterialSpecId.Value, itemKind, quantity,
                UsableWidthIn, UsableLengthIn, ShapeClassification, IsQuarantined, Notes);
            var result = await openingBalance.RecordOpeningBalanceAsync(CorrelationId, request, userId);
            return RedirectToPage("Success", new { code = result.PublicItemCode });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or MaterialLedger.Data.Idempotency.OperationConflictException)
        {
            Error = ex is MaterialLedger.Data.Idempotency.OperationConflictException
                ? "This form was already submitted with different values — please review and resubmit."
                : ex.Message;
            return Page();
        }
    }

    private Task<List<MaterialSpec>> LoadAvailableSpecsAsync() => db.MaterialSpecs
        .AsNoTracking()
        .Where(x => x.IsActive)
        .OrderBy(x => x.MaterialFamily).ThenBy(x => x.Grade).ThenBy(x => x.Finish).ThenBy(x => x.ThicknessIn)
        .ToListAsync();
}
