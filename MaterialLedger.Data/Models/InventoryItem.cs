namespace MaterialLedger.Data.Models;

public enum InventoryItemKind { FullSheetBundle, FullSheet, Drop }
public enum InventoryItemStatus { Available, Reserved, AwaitingCloseout, Quarantined, Depleted, Scrapped }
public enum ShapeClassification { Rectangular, IrregularConservativeRectangle, Unknown }

public class InventoryItem
{
    public long InventoryItemId { get; set; }
    public string PublicItemCode { get; set; } = string.Empty;
    public long MaterialSpecId { get; set; }
    public MaterialSpec? MaterialSpec { get; set; }
    public long? SheetLotId { get; set; }
    public SheetLot? SheetLot { get; set; }
    public long InventoryLocationId { get; set; }
    public InventoryLocation? InventoryLocation { get; set; }
    public InventoryItemKind ItemKind { get; set; }
    public InventoryItemStatus Status { get; set; }
    public decimal QuantityOnHand { get; set; }
    public decimal? UsableWidthIn { get; set; }
    public decimal? UsableLengthIn { get; set; }
    public ShapeClassification ShapeClassification { get; set; }
    public DateTime LastObservedAtUtc { get; set; }
    public int LastObservedBy { get; set; }
    public AppUser? LastObservedByUser { get; set; }
    public string? Notes { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
