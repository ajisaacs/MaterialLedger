namespace MaterialLedger.Data.Models;

public enum AttachmentEntityType { InventoryItem, MaterialReservation, MaterialTransaction, MaterialException }

public class Attachment
{
    public long AttachmentId { get; set; }
    public AttachmentEntityType EntityType { get; set; }
    public long EntityId { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public string? Checksum { get; set; }
    public DateTime UploadedAtUtc { get; set; }
    public int UploadedBy { get; set; }
    public AppUser? UploadedByUser { get; set; }
}
