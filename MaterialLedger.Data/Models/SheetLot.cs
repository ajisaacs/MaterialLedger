namespace MaterialLedger.Data.Models;

public enum SheetLotSourceType { OpeningBalance, Receipt, Adjustment }

public class SheetLot
{
    public long SheetLotId { get; set; }
    public long MaterialSpecId { get; set; }
    public MaterialSpec? MaterialSpec { get; set; }
    public string? VendorName { get; set; }
    public string? PurchaseOrderNumber { get; set; }
    public string? ReceiptNumber { get; set; }
    public string? VendorPartNumber { get; set; }
    public string? HeatLotNumber { get; set; }
    public decimal? CostPerUnit { get; set; }
    public DateTime? ReceivedAtUtc { get; set; }
    public SheetLotSourceType SourceType { get; set; }
}
