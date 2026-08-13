using System.Security.Cryptography;
using MaterialLedger.Data.Models;

namespace MaterialLedger.Data.Attachments;

public class AttachmentStorageService(MaterialLedgerDbContext db, string storageRootPath, Action<string>? deleteFile = null)
{
    private readonly string _root = Path.GetFullPath(storageRootPath);
    private readonly Action<string> _deleteFile = deleteFile ?? File.Delete;

    public async Task<Attachment> SaveAsync(AttachmentEntityType entityType, long entityId, string fileName, string? contentType, Stream content, int uploadedBy, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var displayName = Path.GetFileName(fileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(displayName)) displayName = "attachment";
        var extension = Path.GetExtension(displayName);
        var storedFileName = $"{entityType}-{entityId}-{Guid.NewGuid():N}{extension}";
        var finalPath = ResolvePath(storedFileName);
        var tempPath = ResolvePath($".{Guid.NewGuid():N}.uploading");
        long byteLength = 0;
        string checksum;
        try
        {
            using var sha = SHA256.Create();
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    byteLength += read;
                }
                sha.TransformFinalBlock([], 0, 0);
            }
            checksum = Convert.ToHexString(sha.Hash!);
            File.Move(tempPath, finalPath);
            var attachment = new Attachment { EntityType = entityType, EntityId = entityId, StoragePath = storedFileName, OriginalFileName = displayName, ContentType = contentType, ByteLength = byteLength, Checksum = checksum, UploadedAtUtc = DateTime.UtcNow, UploadedBy = uploadedBy };
            db.Attachments.Add(attachment);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                try
                {
                    db.Entry(attachment).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
                }
                catch
                {
                    // The context may itself be disposed; retain the persistence exception.
                }

                BestEffortDelete(finalPath);
                throw;
            }

            return attachment;
        }
        catch
        {
            BestEffortDelete(tempPath);
            throw;
        }
    }

    private void BestEffortDelete(string path)
    {
        try
        {
            if (File.Exists(path)) _deleteFile(path);
        }
        catch
        {
            // Cleanup must never obscure the stream or database persistence failure.
        }
    }

    public FileStream OpenRead(Attachment attachment) => new(ResolvePath(attachment.StoragePath, requireExisting: true), FileMode.Open, FileAccess.Read, FileShare.Read);
    public string GetFullPath(Attachment attachment) => ResolvePath(attachment.StoragePath);

    private string ResolvePath(string storagePath, bool requireExisting = false)
    {
        var path = Path.GetFullPath(Path.Combine(_root, storagePath));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !string.Equals(path, _root, StringComparison.Ordinal))
            throw new InvalidOperationException("Attachment storage path is invalid.");
        if (requireExisting && !File.Exists(path)) throw new FileNotFoundException("Attachment file was not found.");
        return path;
    }
}
