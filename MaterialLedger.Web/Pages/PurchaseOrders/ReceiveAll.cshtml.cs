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

    private Task<PurchaseOrder?> LoadOrderAsync() => db.PurchaseOrders
        .Include(x => x.Lines).ThenInclude(x => x.MaterialSpec)
        .Include(x => x.Lines).ThenInclude(x => x.LocationAllocations).ThenInclude(x => x.InventoryLocation)
        .FirstOrDefaultAsync(x => x.PurchaseOrderId == Id);

    private static List<ReceiveAllRow> BuildRows(PurchaseOrder order)
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
