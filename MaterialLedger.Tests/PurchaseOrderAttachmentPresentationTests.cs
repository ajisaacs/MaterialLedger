using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Attachments;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.PurchaseOrders;
using MaterialLedger.Web.Attachments;
using MaterialLedger.Web.Pages.PurchaseOrders;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace MaterialLedger.Tests;

public sealed class PurchaseOrderAttachmentPresentationTests : IDisposable
{
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"purchase-order-attachment-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("text/plain", "confirmation.pdf")]
    [InlineData("application/pdf", "confirmation.txt")]
    public async Task UploadAttachment_rejects_disallowed_claimed_type_or_extension(string contentType, string fileName)
    {
        await using var db = CreateDb();
        await AddPurchaseOrderAsync(db);
        var model = NewDetailsModel(db, CreateUploadPolicy());
        SetUploadedFile(model, CreateFile(contentType, fileName));

        var action = await model.OnPostUploadAttachmentAsync();

        Assert.IsType<PageResult>(action);
        Assert.Equal("The uploaded file type is not allowed.", model.Error);
        Assert.Empty(await db.Attachments.ToListAsync());
    }

    [Fact]
    public async Task UploadAttachment_accepts_allowed_file_and_preserves_claimed_type()
    {
        await using var db = CreateDb();
        await AddPurchaseOrderAsync(db);
        var model = NewDetailsModel(db, CreateUploadPolicy());
        SetUploadedFile(model, CreateFile("application/pdf", "confirmation.pdf"));

        var action = await model.OnPostUploadAttachmentAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(action);
        Assert.Equal(41L, redirect.RouteValues!["id"]);
        var attachment = Assert.Single(await db.Attachments.ToListAsync());
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal("confirmation.pdf", attachment.OriginalFileName);
    }

    [Fact]
    public async Task UploadAttachment_rejects_policy_oversize_without_saving()
    {
        await using var db = CreateDb();
        await AddPurchaseOrderAsync(db);
        var model = NewDetailsModel(db, CreateUploadPolicy());
        SetUploadedFile(model, CreateFile("application/pdf", "large.pdf", 1025));

        var action = await model.OnPostUploadAttachmentAsync();

        Assert.IsType<PageResult>(action);
        Assert.Equal("The uploaded file is too large.", model.Error);
        Assert.Empty(await db.Attachments.ToListAsync());
    }

    [Fact]
    public async Task UploadAttachment_converts_multipart_parser_oversize_to_friendly_error_without_saving()
    {
        await using var db = CreateDb();
        await AddPurchaseOrderAsync(db);
        var model = NewDetailsModel(db, CreateUploadPolicy());
        model.HttpContext.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(new ThrowingFormFeature());

        var action = await model.OnPostUploadAttachmentAsync();

        Assert.IsType<PageResult>(action);
        Assert.Equal("The uploaded file is too large.", model.Error);
        Assert.Empty(await db.Attachments.ToListAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true);
    }

    private static MaterialLedgerDbContext CreateDb() => new(new DbContextOptionsBuilder<MaterialLedgerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private DetailsModel NewDetailsModel(MaterialLedgerDbContext db, AttachmentUploadPolicy policy) => new(
        db,
        new PurchaseOrderService(db, new OperationLogGuard(db)),
        new AttachmentStorageService(db, _storageRoot),
        policy)
    {
        Id = 41,
        PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "7")], "test"))
            }
        }
    };

    private static AttachmentUploadPolicy CreateUploadPolicy() => new(Options.Create(new AttachmentUploadOptions
    {
        MaxAttachmentBytes = 1024,
        AllowedAttachmentMediaTypes = ["application/pdf", "image/png", "image/jpeg"],
        AllowedAttachmentExtensions = [".pdf", ".png", ".jpg", ".jpeg"]
    }));

    private static void SetUploadedFile(DetailsModel model, IFormFile file) =>
        model.HttpContext.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(new StaticFormFeature(new FormCollection([], new FormFileCollection { file })));

    private static IFormFile CreateFile(string contentType, string fileName, int length = 10)
    {
        var file = new FormFile(new MemoryStream(new byte[length]), 0, length, "File", fileName);
        file.Headers = new HeaderDictionary { [HeaderNames.ContentType] = contentType };
        return file;
    }

#pragma warning disable CS8767 // IFormFeature's oblivious setter nullability cannot be expressed in test doubles.
    private sealed class StaticFormFeature(IFormCollection form) : Microsoft.AspNetCore.Http.Features.IFormFeature
    {
        public bool HasFormContentType => true;
        public IFormCollection Form { get; [param: AllowNull] set; } = form;
        public IFormCollection ReadForm() => Form;
        public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken) => Task.FromResult(Form);
    }

    private sealed class ThrowingFormFeature : Microsoft.AspNetCore.Http.Features.IFormFeature
    {
        public bool HasFormContentType => true;
        public IFormCollection Form { get => throw new InvalidDataException(); [param: AllowNull] set => throw new NotSupportedException(); }
        public IFormCollection ReadForm() => throw new InvalidDataException();
        public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken) => Task.FromException<IFormCollection>(new InvalidDataException());
    }
#pragma warning restore CS8767

    private static async Task AddPurchaseOrderAsync(MaterialLedgerDbContext db)
    {
        db.PurchaseOrders.Add(new PurchaseOrder
        {
            PurchaseOrderId = 41,
            VendorName = "Vendor",
            PurchaseOrderNumber = "PO-41",
            CreatedBy = 7,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
