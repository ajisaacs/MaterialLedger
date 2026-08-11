// MaterialLedger.Web/Pages/PurchaseOrders/ReceiveAll.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.PurchaseOrders;
using MaterialLedger.Data.Receiving;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.PurchaseOrders;

public class ReceiveAllRow
{
    public Guid CorrelationId { get; set; }
    public long? AllocationId { get; set; }
    public long PurchaseOrderLineId { get; set; }
    public int LineNumber { get; set; }
    public string? MaterialDisplayName { get; set; }
    public bool Selected { get; set; }
    public long? LocationId { get; set; }
    public string? LocationDisplay { get; set; }
    public decimal Quantity { get; set; }
    public string? HeatLotNumber { get; set; }
    public decimal? CostPerUnit { get; set; }
    public bool IsQuarantined { get; set; }
    public string? Notes { get; set; }
    public string? SucceededItemCode { get; set; }
    public string? Error { get; set; }
    public string? Warning { get; set; }
}

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class ReceiveAllModel(MaterialLedgerDbContext db, ReceiveMaterialService receiveMaterial, PurchaseOrderService purchaseOrderService) : PageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty] public List<ReceiveAllRow> Rows { get; set; } = [];

    public PurchaseOrder? Order { get; set; }
    public List<InventoryLocation> LeafLocations { get; set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        Order = await LoadOrderAsync();
        if (Order is null) return NotFound();

        LeafLocations = await ActiveMaterialSlotQuery.Create(db).ToListAsync();
        Rows = BuildRows(Order);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Order = await LoadOrderAsync();
        if (Order is null) return NotFound();
        LeafLocations = await ActiveMaterialSlotQuery.Create(db).ToListAsync();

        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        foreach (var row in Rows)
        {
            row.Error = null;
            row.Warning = null;
            if (!row.Selected) continue;

            if (row.LocationId is null)
            {
                row.Error = "Choose a location.";
                continue;
            }
            if (row.Quantity < 1 || row.Quantity != decimal.Truncate(row.Quantity))
            {
                row.Error = "Quantity must be a whole number of at least 1.";
                continue;
            }

            var line = Order.Lines.FirstOrDefault(x => x.PurchaseOrderLineId == row.PurchaseOrderLineId);
            if (line is null)
            {
                row.Error = "This line no longer exists.";
                continue;
            }

            try
            {
                var request = new ReceiveMaterialRequest(
                    InventoryLocationId: row.LocationId.Value,
                    MaterialSpecId: line.MaterialSpecId,
                    ItemKind: InventoryItemKind.FullSheetBundle,
                    Quantity: row.Quantity,
                    UsableWidthIn: line.UsableWidthIn,
                    UsableLengthIn: line.UsableLengthIn,
                    ShapeClassification: ShapeClassification.Rectangular,
                    IsQuarantined: row.IsQuarantined,
                    VendorName: Order.VendorName,
                    PurchaseOrderNumber: Order.PurchaseOrderNumber,
                    ReceiptNumber: null,
                    VendorPartNumber: line.VendorPartNumber,
                    HeatLotNumber: row.HeatLotNumber,
                    CostPerUnit: row.CostPerUnit,
                    Notes: row.Notes,
                    PurchaseOrderLineId: line.PurchaseOrderLineId);

                var result = await receiveMaterial.ReceiveMaterialAsync(row.CorrelationId, request, userId);
                row.SucceededItemCode = result.PublicItemCode;
                row.LocationDisplay = LeafLocations.FirstOrDefault(x => x.InventoryLocationId == row.LocationId)?.LocationCode;

                if (row.AllocationId is not null)
                {
                    try
                    {
                        await purchaseOrderService.RemoveLocationAllocationAsync(Guid.NewGuid(), row.AllocationId.Value, userId);
                    }
                    catch (Exception)
                    {
                        foreach (var entry in db.ChangeTracker.Entries<PurchaseOrderLineAllocation>()
                            .Where(e => e.State == EntityState.Deleted))
                        {
                            entry.State = EntityState.Unchanged;
                        }
                        row.Warning = "Received, but its plan entry couldn't be cleared — remove it manually.";
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationConflictException)
            {
                row.Error = ex is OperationConflictException
                    ? "This row was already submitted with different values — please review and resubmit."
                    : ex.Message;
            }
            catch (Exception ex)
            {
                row.Error = $"Unexpected error: {ex.Message}";
            }
        }

        return Page();
    }

    private Task<PurchaseOrder?> LoadOrderAsync() => db.PurchaseOrders
        .Include(x => x.Lines).ThenInclude(x => x.MaterialSpec)
        .Include(x => x.Lines).ThenInclude(x => x.LocationAllocations).ThenInclude(x => x.InventoryLocation)
        .FirstOrDefaultAsync(x => x.PurchaseOrderId == Id);

    public static List<ReceiveAllRow> BuildRows(PurchaseOrder order)
    {
        var rows = new List<ReceiveAllRow>();
        foreach (var line in order.Lines
            .Where(x => x.Status is PurchaseOrderLineStatus.Open or PurchaseOrderLineStatus.PartiallyReceived)
            .OrderBy(x => x.LineNumber))
        {
            foreach (var allocation in line.LocationAllocations.OrderBy(x => x.PurchaseOrderLineAllocationId))
            {
                rows.Add(new ReceiveAllRow
                {
                    CorrelationId = Guid.NewGuid(),
                    AllocationId = allocation.PurchaseOrderLineAllocationId,
                    PurchaseOrderLineId = line.PurchaseOrderLineId,
                    LineNumber = line.LineNumber,
                    MaterialDisplayName = line.MaterialSpec?.DisplayName,
                    Selected = true,
                    LocationId = allocation.InventoryLocationId,
                    LocationDisplay = allocation.InventoryLocation?.LocationCode,
                    Quantity = allocation.QuantityPlanned,
                    CostPerUnit = line.UnitPrice
                });
            }

            var allocated = line.LocationAllocations.Sum(x => x.QuantityPlanned);
            var unallocated = line.QuantityOrdered - line.QuantityReceived - allocated;
            if (unallocated > 0)
            {
                rows.Add(new ReceiveAllRow
                {
                    CorrelationId = Guid.NewGuid(),
                    AllocationId = null,
                    PurchaseOrderLineId = line.PurchaseOrderLineId,
                    LineNumber = line.LineNumber,
                    MaterialDisplayName = line.MaterialSpec?.DisplayName,
                    Selected = false,
                    LocationId = null,
                    Quantity = unallocated,
                    CostPerUnit = line.UnitPrice
                });
            }
        }
        return rows;
    }
}
