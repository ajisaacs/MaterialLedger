namespace MaterialLedger.Data.Models;

public class MaterialSpec
{
    public long MaterialSpecId { get; set; }
    public string MaterialFamily { get; set; } = string.Empty;
    public string? Grade { get; set; }
    public string? Finish { get; set; }
    public decimal ThicknessIn { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? GaugeLabel { get; set; }
    public bool IsActive { get; set; }

    // Persisted computed column; set by the database, never by app code.
    public string NormalizedKey { get; private set; } = string.Empty;
}
