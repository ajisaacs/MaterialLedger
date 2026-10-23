namespace MaterialLedger.Data.Models;

public enum MaterialTransactionType
{
    OpeningBalance, Receipt, Reservation, ReservationRelease, Return,
    Consumption, RemnantCreated, Transfer, Adjustment, Scrap, Quarantine
}
public enum MaterialTransactionSourceSystem { OperatorUI, LaserDashboard, AdministrativeAdjustment }

public class MaterialTransaction
{
    public long MaterialTransactionId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public int RecordedBy { get; set; }
    public AppUser? RecordedByUser { get; set; }
    public MaterialTransactionType TransactionType { get; set; }
    public long InventoryItemId { get; set; }
    public InventoryItem? InventoryItem { get; set; }
    public long? RelatedInventoryItemId { get; set; }
    public InventoryItem? RelatedInventoryItem { get; set; }
    public long? MaterialReservationId { get; set; }
    public MaterialReservation? MaterialReservation { get; set; }
    public long? RelatedInventoryLocationId { get; set; }
    public InventoryLocation? RelatedInventoryLocation { get; set; }
    public long? SheetLotId { get; set; }
    public SheetLot? SheetLot { get; set; }
    public decimal QuantityDelta { get; set; }
    public decimal? GeometryWidthInSnapshot { get; set; }
    public decimal? GeometryLengthInSnapshot { get; set; }
    public string? ReasonCode { get; set; }
    public string? Notes { get; set; }
    public MaterialTransactionSourceSystem SourceSystem { get; set; }
    public string? ExternalReference { get; set; }
}
