namespace MaterialLedger.Data.Models;

public enum PurchaseOrderLineStatus { Open, PartiallyReceived, Received, Cancelled }

public class PurchaseOrder
{
    public long PurchaseOrderId { get; set; }
    public long VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    public string PurchaseOrderNumber { get; set; } = string.Empty;
    public DateOnly? OrderDate { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedBy { get; set; }
    public AppUser? CreatedByUser { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];
}

public class PurchaseOrderLine
{
    public long PurchaseOrderLineId { get; set; }
    public long? VendorPartId { get; set; }
    public VendorPart? VendorPart { get; set; }
    public long PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public int LineNumber { get; set; }
    public long MaterialSpecId { get; set; }
    public MaterialSpec? MaterialSpec { get; set; }
    public string? VendorDescription { get; set; }

    public decimal QuantityOrdered { get; set; }
    public decimal QuantityReceived { get; set; }
    public decimal? UsableWidthIn { get; set; }
    public decimal? UsableLengthIn { get; set; }
    public decimal? UnitPrice { get; set; }
    public DateOnly? QuotedDeliveryDate { get; set; }
    public PurchaseOrderLineStatus Status { get; set; }
    public List<PurchaseOrderLineAllocation> LocationAllocations { get; set; } = [];
}

public class PurchaseOrderLineAllocation
{
    public long PurchaseOrderLineAllocationId { get; set; }
    public long PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    public long InventoryLocationId { get; set; }
    public InventoryLocation? InventoryLocation { get; set; }
    public decimal QuantityPlanned { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedBy { get; set; }
    public AppUser? CreatedByUser { get; set; }
}
