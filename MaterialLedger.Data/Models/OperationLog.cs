namespace MaterialLedger.Data.Models;

public class OperationLog
{
    public Guid CorrelationId { get; set; }
    public string ProcedureName { get; set; } = string.Empty;
    public string RequestPayloadHash { get; set; } = string.Empty;
    public string ResultSummary { get; set; } = string.Empty;
    public DateTime ExecutedAtUtc { get; set; }
    public int ExecutedBy { get; set; }
    public AppUser? ExecutedByUser { get; set; }
}
