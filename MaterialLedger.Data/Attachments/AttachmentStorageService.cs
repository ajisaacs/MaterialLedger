using System.Security.Cryptography;
using MaterialLedger.Data.Models;

namespace MaterialLedger.Data.Attachments;

public class AttachmentStorageService(MaterialLedgerDbContext db, string storageRootPath)
{
    public async Task<Attachment> SaveAsync(
        AttachmentEntityType entityType, long entityId, string fileName, string? contentType, Stream content, int uploadedBy)
    {
        Directory.CreateDirectory(storageRootPath);
        var storedFileName = $"{entityType}-{entityId}-{Guid.NewGuid():N}{Path.GetExtension(fileName)}";
        var fullPath = Path.Combine(storageRootPath, storedFileName);

        await using (var fileStream = File.Create(fullPath))
        {
            await content.CopyToAsync(fileStream);
        }

        var checksum = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullPath)));

        var attachment = new Attachment
        {
            EntityType = entityType,
            EntityId = entityId,
            StoragePath = storedFileName,
            ContentType = contentType,
            Checksum = checksum,
            UploadedAtUtc = DateTime.UtcNow,
            UploadedBy = uploadedBy
        };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();
        return attachment;
    }

    public string GetFullPath(Attachment attachment) => Path.Combine(storageRootPath, attachment.StoragePath);
}
