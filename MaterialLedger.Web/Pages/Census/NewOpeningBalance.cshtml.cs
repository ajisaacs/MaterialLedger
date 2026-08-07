using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Census;
using MaterialLedger.Data.Materials;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Census;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class NewOpeningBalanceModel(MaterialLedgerDbContext db, OpeningBalanceService openingBalance, MaterialSpecService materialSpecs) : PageModel
{
    [BindProperty] public long LocationId { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public long? ExistingMaterialSpecId { get; set; }
    [BindProperty] public string MaterialFamily { get; set; } = string.Empty;
    [BindProperty] public string? Grade { get; set; }
    [BindProperty] public string? Finish { get; set; }
    [BindProperty] public decimal ThicknessIn { get; set; }
    [BindProperty] public InventoryItemKind ItemKind { get; set; } = InventoryItemKind.FullSheetBundle;
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
        if (Location is null)
            return NotFound();

        LocationId = locationId;
        CorrelationId = Guid.NewGuid();
        ExistingActiveItems = (await openingBalance.GetActiveItemsAtLocationAsync(locationId)).ToList();
        AvailableSpecs = (await materialSpecs.SearchAsync(null)).ToList();

        if (copyFromItemId is not null)
        {
            var prior = ExistingActiveItems.FirstOrDefault(x => x.InventoryItemId == copyFromItemId);
            if (prior is not null)
            {
                ExistingMaterialSpecId = prior.MaterialSpecId;
                ItemKind = prior.ItemKind;
                UsableWidthIn = prior.UsableWidthIn;
                UsableLengthIn = prior.UsableLengthIn;
                ShapeClassification = prior.ShapeClassification;
                // Quantity is intentionally left at its default (0) — the operator must confirm a
                // real count, never inherit the prior item's quantity (spec section 4A).
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Location = await db.InventoryLocations.FindAsync(LocationId);
        ExistingActiveItems = (await openingBalance.GetActiveItemsAtLocationAsync(LocationId)).ToList();
        AvailableSpecs = (await materialSpecs.SearchAsync(null)).ToList();

        try
        {
            var specId = ExistingMaterialSpecId
                ?? (await materialSpecs.GetOrCreateAsync(MaterialFamily, Grade, Finish, ThicknessIn,
                    $"{MaterialFamily} {Grade} {ThicknessIn}in".Trim())).MaterialSpecId;

            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new OpeningBalanceRequest(LocationId, specId, ItemKind, Quantity,
                UsableWidthIn, UsableLengthIn, ShapeClassification, IsQuarantined, Notes);

            var result = await openingBalance.RecordOpeningBalanceAsync(CorrelationId, request, userId);
            return RedirectToPage("Success", new { code = result.PublicItemCode });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Error = ex.Message;
            return Page();
        }
    }
}
