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
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("fake pdf bytes"));

        var attachment = await service.SaveAsync(AttachmentEntityType.PurchaseOrder, 42, "confirmation.pdf", "application/pdf", content, user.AppUserId);

        Assert.True(File.Exists(service.GetFullPath(attachment)));
        Assert.EndsWith(".pdf", attachment.StoragePath);
        Assert.NotNull(attachment.Checksum);

        var stored = await db.Attachments.SingleAsync(x => x.AttachmentId == attachment.AttachmentId);
        Assert.Equal(AttachmentEntityType.PurchaseOrder, stored.EntityType);
        Assert.Equal(42, stored.EntityId);
        Assert.Equal("application/pdf", stored.ContentType);
    }
}
