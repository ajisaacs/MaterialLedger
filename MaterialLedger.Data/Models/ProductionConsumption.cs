namespace MaterialLedger.Data.Models;

public enum ProductionConsumptionOutcome { AllConsumed, FullSheetReturned, UsableDropCreated, NeedsReview }

public class ProductionConsumption
{
    public long ProductionConsumptionId { get; set; }
    public long MaterialReservationId { get; set; }
    public MaterialReservation? MaterialReservation { get; set; }
    public long PrimaryMaterialTransactionId { get; set; }
    public MaterialTransaction? PrimaryMaterialTransaction { get; set; }
    public string? LaserProgramRecordId { get; set; }
    public DateTime ConfirmedAtUtc { get; set; }
    public int ConfirmedBy { get; set; }
    public AppUser? ConfirmedByUser { get; set; }
    public ProductionConsumptionOutcome Outcome { get; set; }
    public decimal? ActualConsumedQuantity { get; set; }
    public decimal? ActualConsumedAreaSqIn { get; set; }
    public string? Notes { get; set; }
}
