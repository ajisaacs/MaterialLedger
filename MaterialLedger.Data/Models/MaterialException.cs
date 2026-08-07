namespace MaterialLedger.Data.Models;

public enum MaterialExceptionType
{
    AwaitingCloseoutOverdue, OutcomeUnknown, QuarantinedMaterial,
    GeometryMismatchOrInsufficientBalance, StaleObservation
}
public enum MaterialExceptionStatus { Open, Fixed }

public class MaterialException
{
    public long MaterialExceptionId { get; set; }
    public MaterialExceptionType ExceptionType { get; set; }
    public long? InventoryItemId { get; set; }
    public InventoryItem? InventoryItem { get; set; }
    public long? MaterialReservationId { get; set; }
    public MaterialReservation? MaterialReservation { get; set; }
    public string DedupeKey { get; set; } = string.Empty;
    public MaterialExceptionStatus Status { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public int? OpenedBy { get; set; }
    public AppUser? OpenedByUser { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public int? ResolvedBy { get; set; }
    public AppUser? ResolvedByUser { get; set; }
    public string? ResolutionNotes { get; set; }
    public string? Notes { get; set; }
}
