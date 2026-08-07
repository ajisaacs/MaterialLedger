namespace MaterialLedger.Data.Models;

public enum ExternalMappingStatus { Proposed, Approved }

public class MaterialSpecExternalMapping
{
    public long MaterialSpecExternalMappingId { get; set; }
    public long MaterialSpecId { get; set; }
    public MaterialSpec? MaterialSpec { get; set; }
    public string ExternalSystem { get; set; } = "PEP";
    public string ExternalKey { get; set; } = string.Empty;
    public ExternalMappingStatus Status { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public int? ApprovedBy { get; set; }
    public AppUser? ApprovedByUser { get; set; }
}
