namespace MaterialLedger.Data.Models;

public enum InventoryLocationType { Area, Rack, Shelf, Bay, Staging, Quarantine, Scrap }
public enum InventoryStorageMode { RolloutShelf, VerticalDropBay, FloorStaging, Other }

public class InventoryLocation
{
    public long InventoryLocationId { get; set; }
    public long? ParentInventoryLocationId { get; set; }
    public InventoryLocation? ParentInventoryLocation { get; set; }
    public string LocationCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public InventoryLocationType LocationType { get; set; }
    public InventoryStorageMode StorageMode { get; set; }
    public int SortOrder { get; set; }
    public bool CapacityConfirmed { get; set; }
    public bool IsActive { get; set; }
    public string? Notes { get; set; }
}
