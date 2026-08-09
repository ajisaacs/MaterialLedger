using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Materials;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Receiving;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Receiving;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class NewReceiptModel(MaterialLedgerDbContext db, ReceiveMaterialService receiveMaterial, MaterialSpecService materialSpecs) : PageModel
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
    [BindProperty] public string? VendorName { get; set; }
    [BindProperty] public string PurchaseOrderNumber { get; set; } = string.Empty;
    [BindProperty] public string? ReceiptNumber { get; set; }
    [BindProperty] public string? VendorPartNumber { get; set; }
    [BindProperty] public string? HeatLotNumber { get; set; }
    [BindProperty] public decimal? CostPerUnit { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public InventoryLocation? Location { get; set; }
    public List<MaterialSpec> AvailableSpecs { get; set; } = [];
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long locationId)
    {
        Location = await db.InventoryLocations.FindAsync(locationId);
        if (Location is null)
            return NotFound();

        LocationId = locationId;
        CorrelationId = Guid.NewGuid();
        AvailableSpecs = (await materialSpecs.SearchAsync(null)).ToList();

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Location = await db.InventoryLocations.FindAsync(LocationId);
        AvailableSpecs = (await materialSpecs.SearchAsync(null)).ToList();

        if (ExistingMaterialSpecId is null && (string.IsNullOrWhiteSpace(MaterialFamily) || ThicknessIn <= 0))
        {
            Error = "Select an existing material or enter a family and thickness for a new one.";
            return Page();
        }

        if (ExistingMaterialSpecId is not null && AvailableSpecs.All(s => s.MaterialSpecId != ExistingMaterialSpecId))
        {
            Error = "The selected existing material is no longer available. Please choose another.";
            return Page();
        }

        try
        {
            var specId = ExistingMaterialSpecId
                ?? (await materialSpecs.GetOrCreateAsync(MaterialFamily, Grade, Finish, ThicknessIn,
                    $"{MaterialFamily} {Grade} {ThicknessIn}in".Trim())).MaterialSpecId;

            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new ReceiveMaterialRequest(LocationId, specId, ItemKind, Quantity,
                UsableWidthIn, UsableLengthIn, ShapeClassification, IsQuarantined,
                VendorName, PurchaseOrderNumber, ReceiptNumber, VendorPartNumber, HeatLotNumber, CostPerUnit, Notes);

            var result = await receiveMaterial.ReceiveMaterialAsync(CorrelationId, request, userId);
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
}
