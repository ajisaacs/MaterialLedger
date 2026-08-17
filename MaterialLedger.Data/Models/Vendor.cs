namespace MaterialLedger.Data.Models;

public class Vendor
{
    public long VendorId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class VendorPart
{
    public long VendorPartId { get; set; }
    public long VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    public string PartNumber { get; set; } = string.Empty;
    public string NormalizedPartNumber { get; private set; } = string.Empty;
    public string? Description { get; set; }
    public decimal? MinimumOrderQty { get; set; }
    public string? Unit { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}