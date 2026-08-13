using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Receiving;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Receiving;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class NewReceiptModel(MaterialLedgerDbContext db, ReceiveMaterialService receiveMaterial) : PageModel
{
    [BindProperty] public long LocationId { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public long? PoLineId { get; set; }
    [BindProperty] public long? ExistingMaterialSpecId { get; set; }
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
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    public List<MaterialSpec> AvailableSpecs { get; set; } = [];
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long locationId, long? poLineId)
    {
        Location = await db.InventoryLocations.FindAsync(locationId);
        if (Location is null) return NotFound();

        LocationId = locationId;
        PoLineId = poLineId;
        CorrelationId = Guid.NewGuid();
        AvailableSpecs = await LoadAvailableSpecsAsync();

        if (poLineId is not null)
        {
            PurchaseOrderLine = await db.PurchaseOrderLines
                .Include(x => x.PurchaseOrder)
                .FirstOrDefaultAsync(x => x.PurchaseOrderLineId == poLineId);
            if (PurchaseOrderLine is not null)
            {
                ExistingMaterialSpecId = PurchaseOrderLine.MaterialSpecId;
                VendorName = PurchaseOrderLine.PurchaseOrder!.VendorName;
                PurchaseOrderNumber = PurchaseOrderLine.PurchaseOrder.PurchaseOrderNumber;
                VendorPartNumber = PurchaseOrderLine.VendorPartNumber;
                CostPerUnit = PurchaseOrderLine.UnitPrice;
                UsableWidthIn = PurchaseOrderLine.UsableWidthIn;
                UsableLengthIn = PurchaseOrderLine.UsableLengthIn;
            }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Location = await db.InventoryLocations.FindAsync(LocationId);
        AvailableSpecs = await LoadAvailableSpecsAsync();
        if (PoLineId is not null)
        {
            PurchaseOrderLine = await db.PurchaseOrderLines
                .Include(x => x.PurchaseOrder)
                .FirstOrDefaultAsync(x => x.PurchaseOrderLineId == PoLineId);
        }

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
            var request = new ReceiveMaterialRequest(
                InventoryLocationId: LocationId,
                MaterialSpecId: ExistingMaterialSpecId.Value,
                ItemKind: ItemKind,
                Quantity: Quantity,
                UsableWidthIn: UsableWidthIn,
                UsableLengthIn: UsableLengthIn,
                ShapeClassification: ShapeClassification,
                IsQuarantined: IsQuarantined,
                VendorName: VendorName,
                PurchaseOrderNumber: PurchaseOrderNumber,
                ReceiptNumber: ReceiptNumber,
                VendorPartNumber: VendorPartNumber,
                HeatLotNumber: HeatLotNumber,
                CostPerUnit: CostPerUnit,
                Notes: Notes,
                PurchaseOrderLineId: PoLineId);

            var result = await receiveMaterial.ReceiveMaterialAsync(CorrelationId, request, userId);
            return RedirectToPage("Success", new { code = result.Result.PublicItemCode });
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
