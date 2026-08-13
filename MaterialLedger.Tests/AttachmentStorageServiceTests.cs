using System.Security.Cryptography;
using System.Text;
using MaterialLedger.Data;
using MaterialLedger.Data.Attachments;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class AttachmentStorageServiceTests : IClassFixture<SqlServerTestDatabaseFixture>, IDisposable
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"ml-attachments-{Guid.NewGuid():N}");

    public AttachmentStorageServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true);
    }

    [Fact]
    public async Task SaveAsync_WritesFileToDiskAndInsertsAttachmentRow()
    {
        await using var db = _fixture.CreateContext();
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Programmer, IsActive = true, PasswordHash = "x" };
        db.Add(user);
        await db.SaveChangesAsync();

        var service = new AttachmentStorageService(db, _storageRoot);
        var payload = Encoding.UTF8.GetBytes("fake pdf bytes");
        await using var content = new MemoryStream(payload);

        var attachment = await service.SaveAsync(AttachmentEntityType.PurchaseOrder, 42, "confirmation.pdf", "application/pdf", content, user.AppUserId);

        Assert.True(File.Exists(service.GetFullPath(attachment)));
        Assert.EndsWith(".pdf", attachment.StoragePath);
        Assert.Equal(payload.Length, attachment.ByteLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), attachment.Checksum);

        var stored = await db.Attachments.SingleAsync(x => x.AttachmentId == attachment.AttachmentId);
        Assert.Equal(AttachmentEntityType.PurchaseOrder, stored.EntityType);
        Assert.Equal(42, stored.EntityId);
        Assert.Equal("application/pdf", stored.ContentType);
    }

    [Fact]
    public async Task SaveAsync_GeneratedPathCannotEscapeRoot_WhenDisplayNameContainsTraversal()
    {
        await using var db = _fixture.CreateContext();
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Programmer, IsActive = true, PasswordHash = "x" };
        db.Add(user); await db.SaveChangesAsync();
        var service = new AttachmentStorageService(db, _storageRoot);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("safe"));
        var attachment = await service.SaveAsync(AttachmentEntityType.PurchaseOrder, 42, "../../outside.pdf", "application/pdf", content, user.AppUserId);

        Assert.StartsWith(Path.GetFullPath(_storageRoot) + Path.DirectorySeparatorChar, service.GetFullPath(attachment), StringComparison.Ordinal);
        Assert.DoesNotContain("..", attachment.StoragePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_CleansFinalFile_WhenDatabasePersistenceFails()
    {
        await using var db = _fixture.CreateContext();
        var service = new AttachmentStorageService(db, _storageRoot);
        await db.DisposeAsync();
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("orphan"));

        await Assert.ThrowsAnyAsync<Exception>(() => service.SaveAsync(AttachmentEntityType.PurchaseOrder, 42, "orphan.pdf", "application/pdf", content, 1));
        Assert.Empty(Directory.Exists(_storageRoot) ? Directory.GetFiles(_storageRoot) : []);
    }

    [Fact]
    public async Task SaveAsync_CleansTemporaryFile_WhenStreamingFails()
    {
        await using var db = _fixture.CreateContext();
        var service = new AttachmentStorageService(db, _storageRoot);

        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(
            AttachmentEntityType.PurchaseOrder, 42, "broken.pdf", "application/pdf", new ThrowingReadStream(), 1));

        Assert.Empty(Directory.Exists(_storageRoot)
            ? Directory.EnumerateFiles(_storageRoot, ".*.uploading").ToArray()
            : []);
    }

    [Fact]
    public async Task SaveAsync_PreservesPersistenceFailure_WhenFinalFileCleanupFails()
    {
        await using var db = _fixture.CreateContext();
        var service = new AttachmentStorageService(db, _storageRoot, _ => throw new IOException("cleanup failed"));
        await db.DisposeAsync();
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("orphan"));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => service.SaveAsync(
            AttachmentEntityType.PurchaseOrder, 42, "orphan.pdf", "application/pdf", content, 1));

        Assert.IsNotType<IOException>(exception);
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("read failed");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(new IOException("read failed"));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(new IOException("read failed"));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task OpenRead_RejectsOutsideRootAndMissingFiles()
    {
        await using var db = _fixture.CreateContext();
        var service = new AttachmentStorageService(db, _storageRoot);
        var outside = new Attachment { StoragePath = "../outside.pdf" };
        Assert.Throws<InvalidOperationException>(() => service.OpenRead(outside));
        Assert.Throws<FileNotFoundException>(() => service.OpenRead(new Attachment { StoragePath = "missing.pdf" }));
    }
}
