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
    [BindProperty] public long? VendorId { get; set; }
    [BindProperty] public string PurchaseOrderNumber { get; set; } = string.Empty;
    [BindProperty] public string? ReceiptNumber { get; set; }
    [BindProperty] public long? VendorPartId { get; set; }
    [BindProperty] public string? HeatLotNumber { get; set; }
    [BindProperty] public decimal? CostPerUnit { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public InventoryLocation? Location { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    public List<MaterialSpec> AvailableSpecs { get; set; } = [];
    public List<Vendor> AvailableVendors { get; set; } = [];
    public List<VendorPart> AvailableVendorParts { get; set; } = [];
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long locationId, long? poLineId)
    {
        Location = await db.InventoryLocations.FindAsync(locationId); if (Location is null) return NotFound();
        LocationId = locationId; PoLineId = poLineId; CorrelationId = Guid.NewGuid(); await LoadOptionsAsync();
        if (poLineId is not null) { PurchaseOrderLine = await LoadPoLineAsync(poLineId.Value); if (PurchaseOrderLine is null) return NotFound(); PopulateFromPoLine(); }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Location = await db.InventoryLocations.FindAsync(LocationId); await LoadOptionsAsync();
        if (PoLineId is not null) { PurchaseOrderLine = await LoadPoLineAsync(PoLineId.Value); if (PurchaseOrderLine is null) return NotFound(); PopulateFromPoLine(); }
        if (ExistingMaterialSpecId is null || AvailableSpecs.All(s => s.MaterialSpecId != ExistingMaterialSpecId)) { Error = "Choose an available material and thickness from the catalog."; return Page(); }
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new ReceiveMaterialRequest(LocationId, ExistingMaterialSpecId.Value, ItemKind, Quantity, UsableWidthIn, UsableLengthIn,
                ShapeClassification, IsQuarantined, VendorId, PurchaseOrderNumber, ReceiptNumber, VendorPartId, HeatLotNumber, CostPerUnit, Notes, PoLineId);
            var result = await receiveMaterial.ReceiveMaterialAsync(CorrelationId, request, userId);
            return RedirectToPage("Success", new { code = result.Result.PublicItemCode });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or MaterialLedger.Data.Idempotency.OperationConflictException) { Error = ex.Message; return Page(); }
    }

    private void PopulateFromPoLine() { var line=PurchaseOrderLine!; ExistingMaterialSpecId=line.MaterialSpecId; VendorId=line.PurchaseOrder!.VendorId; PurchaseOrderNumber=line.PurchaseOrder.PurchaseOrderNumber; VendorPartId=line.VendorPartId; CostPerUnit=line.UnitPrice; UsableWidthIn=line.UsableWidthIn; UsableLengthIn=line.UsableLengthIn; }
    private Task<PurchaseOrderLine?> LoadPoLineAsync(long id) => db.PurchaseOrderLines.Include(x=>x.PurchaseOrder).ThenInclude(x=>x!.Vendor).Include(x=>x.VendorPart).SingleOrDefaultAsync(x=>x.PurchaseOrderLineId==id);
    private async Task LoadOptionsAsync() { AvailableSpecs=await db.MaterialSpecs.AsNoTracking().Where(x=>x.IsActive).OrderBy(x=>x.MaterialFamily).ThenBy(x=>x.Grade).ThenBy(x=>x.Finish).ThenBy(x=>x.ThicknessIn).ToListAsync(); AvailableVendors=await db.Vendors.AsNoTracking().Where(x=>x.IsActive).OrderBy(x=>x.Name).ToListAsync(); AvailableVendorParts=await db.VendorParts.AsNoTracking().Where(x=>x.IsActive).OrderBy(x=>x.VendorId).ThenBy(x=>x.PartNumber).ToListAsync(); }
}
