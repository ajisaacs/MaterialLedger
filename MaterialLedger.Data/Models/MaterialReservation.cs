namespace MaterialLedger.Data.Models;

public enum ReservationEquipment { Laser, Plasma, Shear, Other }
public enum ReservationStatus
{
    Proposed, Approved, Released, AwaitingCloseout,
    ClosedConsumed, ClosedReturned, Cancelled, Exception
}
public enum ExpectedRemainderSource { OpenNest, Manual }

public class MaterialReservation
{
    public long MaterialReservationId { get; set; }
    public long InventoryItemId { get; set; }
    public InventoryItem? InventoryItem { get; set; }
    public string? ShopOpsJobId { get; set; }
    public string? DrawingId { get; set; }
    public string? DrawingRevision { get; set; }
    public int? PepNestYear { get; set; }
    public string? PepNestName { get; set; }
    public Guid? OpenNestNestId { get; set; }
    public string? NestName { get; set; }
    public ReservationEquipment? Equipment { get; set; }
    public string? LaserProgramRecordId { get; set; }
    public int ReservedQuantity { get; set; } = 1;
    public decimal? ReservedAreaSqIn { get; set; }
    public ReservationStatus Status { get; set; }
    public DateTime? ScheduledForUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public int? ApprovedBy { get; set; }
    public AppUser? ApprovedByUser { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedBy { get; set; }
    public AppUser? CreatedByUser { get; set; }
    public decimal? ExpectedRemainderWidthIn { get; set; }
    public decimal? ExpectedRemainderLengthIn { get; set; }
    public ExpectedRemainderSource? ExpectedRemainderSourceType { get; set; }
    public List<ExpectedRemnant> ExpectedRemnants { get; set; } = [];
    public byte[] RowVersion { get; set; } = [];
}

public class ExpectedRemnant
{
    public long ExpectedRemnantId { get; set; }
    public long MaterialReservationId { get; set; }
    public MaterialReservation? MaterialReservation { get; set; }
    public int Sequence { get; set; }
    public decimal WidthIn { get; set; }
    public decimal LengthIn { get; set; }
    public ExpectedRemainderSource SourceType { get; set; }
}
