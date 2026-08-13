using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MaterialLedger.Tests.Api;

public class AttachmentEndpointTests
{
    [Fact]
    public async Task UploadThenScopedDownload_ReturnsSafeDtoAndExactBytes()
    {
        using var factory = new AttachmentApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await factory.SeedPurchaseOrderAsync();
        client.DefaultRequestHeaders.Add(TestAuth.RoleHeader, "Programmer");

        using var form = new MultipartFormDataContent();
        var bytes = "attachment bytes"u8.ToArray();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(content, "file", "../confirmation.pdf");
        var response = await client.PostAsync("/api/v1/attachments/purchase-order/41", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AttachmentResponse>();
        Assert.NotNull(dto);
        Assert.Equal("confirmation.pdf", dto!.FileName);
        Assert.Equal("application/pdf", dto.ContentType);
        Assert.Equal(bytes.Length, dto.ByteLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), dto.Checksum);
        Assert.Equal(DateTimeKind.Utc, dto.UploadedAtUtc.Kind);
        Assert.InRange(dto.UploadedAtUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.DoesNotContain("storagePath", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var download = await client.GetAsync(dto.DownloadUrl);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.Contains("confirmation.pdf", download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("unknown", 400, "validation")]
    [InlineData("purchase-order", 999, "not-found")]
    public async Task UploadInvalidScope_ReturnsProblem(string entityType, int entityId, string type)
    {
        using var factory = new AttachmentApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuth.RoleHeader, "Programmer");
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("x"u8.ToArray()), "file", "x.pdf");
        var response = await client.PostAsync($"/api/v1/attachments/{entityType}/{entityId}", form);
        await AssertProblemAsync(response, type == "validation" ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound, type);
    }

    [Fact]
    public async Task UploadWithoutWriteRoleAndDownloadWithoutAuthentication_AreDenied()
    {
        using var factory = new AttachmentApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuth.RoleHeader, "Operator");
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("x"u8.ToArray()), "file", "x.pdf");
        await AssertProblemAsync(await client.PostAsync("/api/v1/attachments/purchase-order/41", form), HttpStatusCode.Forbidden, "forbidden");
        client.DefaultRequestHeaders.Remove(TestAuth.RoleHeader);
        await AssertProblemAsync(await client.GetAsync("/api/v1/attachments/purchase-order/41/1"), HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task DownloadMissingOrMismatchedScope_ReturnsNotFoundProblemWithoutBytes()
    {
        using var factory = new AttachmentApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuth.RoleHeader, "Programmer");
        await factory.SeedPurchaseOrderAsync();

        var missing = await client.GetAsync("/api/v1/attachments/purchase-order/41/999");
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "not-found");
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);

        var mismatched = await client.GetAsync("/api/v1/attachments/purchase-order/42/999");
        await AssertProblemAsync(mismatched, HttpStatusCode.NotFound, "not-found");
        Assert.Equal("application/problem+json", mismatched.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task DownloadExistingScopedAttachmentWithMissingStoredFile_ReturnsNotFoundProblemWithoutBytes()
    {
        using var factory = new AttachmentApiFactory();
        using var client = factory.CreateClient();
        await factory.SeedPurchaseOrderAsync();
        client.DefaultRequestHeaders.Add(TestAuth.RoleHeader, "Programmer");

        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent("attachment bytes"u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(content, "file", "confirmation.pdf");
        var upload = await client.PostAsync("/api/v1/attachments/purchase-order/41", form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var dto = await upload.Content.ReadFromJsonAsync<AttachmentResponse>();
        Assert.NotNull(dto);

        await factory.DeleteStoredFileAsync(dto!.AttachmentId);

        var download = await client.GetAsync(dto.DownloadUrl);
        await AssertProblemAsync(download, HttpStatusCode.NotFound, "not-found");
        Assert.Equal("application/problem+json", download.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("text/plain", "file.pdf")]
    [InlineData("application/pdf", "file.txt")]
    public async Task UploadDisallowedTypeOrExtension_ReturnsUnsupportedMediaType(string contentType, string fileName)
    {
        using var factory = new AttachmentApiFactory();
        using var client = factory.CreateClient();
        await factory.SeedPurchaseOrderAsync();
        client.DefaultRequestHeaders.Add(TestAuth.RoleHeader, "Programmer");
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent("x"u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "file", fileName);

        await AssertProblemAsync(await client.PostAsync("/api/v1/attachments/purchase-order/41", form), HttpStatusCode.UnsupportedMediaType, "unsupported-media-type");
    }

    [Fact]
    public async Task UploadExactlyAtPolicyLimit_IsAcceptedDespiteMultipartOverhead()
    {
        using var factory = new AttachmentApiFactory(maxAttachmentBytes: 1024);
        using var client = factory.CreateClient();
        await factory.SeedPurchaseOrderAsync();
        client.DefaultRequestHeaders.Add(TestAuth.RoleHeader, "Programmer");
        using var form = CreatePdfForm(new byte[1024], "exactly-max.pdf");

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/v1/attachments/purchase-order/41", form)).StatusCode);
        Assert.Equal(1, await factory.AttachmentCountAsync());
    }

    [Fact]
    public async Task UploadParserOversizedMultipart_ReturnsPayloadTooLargeProblem()
    {
        using var factory = new AttachmentApiFactory(maxAttachmentBytes: 1024, multipartBodyLengthLimit: 256);
        using var client = factory.CreateClient();
        await factory.SeedPurchaseOrderAsync();
        client.DefaultRequestHeaders.Add(TestAuth.RoleHeader, "Programmer");
        using var form = CreatePdfForm(new byte[1024], "parser-large.pdf");

        await AssertProblemAsync(await client.PostAsync("/api/v1/attachments/purchase-order/41", form), HttpStatusCode.RequestEntityTooLarge, "payload-too-large");
        Assert.Equal(0, await factory.AttachmentCountAsync());
    }

    [Fact]
    public async Task UploadOversizedOrMissingFile_ReturnsExpectedProblem()
    {
        using var factory = new AttachmentApiFactory(maxAttachmentBytes: 1024);
        using var client = factory.CreateClient();
        await factory.SeedPurchaseOrderAsync();
        client.DefaultRequestHeaders.Add(TestAuth.RoleHeader, "Programmer");

        using var oversized = new MultipartFormDataContent();
        var large = new ByteArrayContent(new byte[1025]);
        large.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        oversized.Add(large, "file", "large.pdf");
        await AssertProblemAsync(await client.PostAsync("/api/v1/attachments/purchase-order/41", oversized), HttpStatusCode.RequestEntityTooLarge, "payload-too-large");

        using var missing = new ByteArrayContent(Array.Empty<byte>());
        await AssertProblemAsync(await client.PostAsync("/api/v1/attachments/purchase-order/41", missing), HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task UnauthenticatedUpload_IsDeniedAndCreatesNoMetadata()
    {
        using var factory = new AttachmentApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await factory.SeedPurchaseOrderAsync();
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent("x"u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(content, "file", "x.pdf");

        await AssertProblemAsync(await client.PostAsync("/api/v1/attachments/purchase-order/41", form), HttpStatusCode.Unauthorized, "unauthenticated");
        Assert.Equal(0, await factory.AttachmentCountAsync());
    }

    private static MultipartFormDataContent CreatePdfForm(byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(content, "file", fileName);
        return form;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string suffix)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"problems/{suffix}", body);
    }

    private sealed record AttachmentResponse(long AttachmentId, string FileName, string ContentType, long ByteLength, string Checksum, DateTime UploadedAtUtc, string DownloadUrl);

    private sealed class AttachmentApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachment-api-{Guid.NewGuid():N}");
        private readonly string _databaseName = $"attachment-api-{Guid.NewGuid():N}";
        private readonly long? _maxAttachmentBytes;
        private readonly long? _multipartBodyLengthLimit;

        public AttachmentApiFactory(long? maxAttachmentBytes = null, long? multipartBodyLengthLimit = null)
        {
            _maxAttachmentBytes = maxAttachmentBytes;
            _multipartBodyLengthLimit = multipartBodyLengthLimit;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (_maxAttachmentBytes is not null)
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Storage:MaxAttachmentBytes"] = _maxAttachmentBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

            builder.UseEnvironment("Testing").ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<MaterialLedgerDbContext>>(); services.RemoveAll<IDbContextOptionsConfiguration<MaterialLedgerDbContext>>(); services.RemoveAll<MaterialLedgerDbContext>();
            services.AddDbContext<MaterialLedgerDbContext>(o => o.UseInMemoryDatabase(_databaseName));
            services.RemoveAll<MaterialLedger.Data.Attachments.AttachmentStorageService>();
            services.AddScoped(sp => new MaterialLedger.Data.Attachments.AttachmentStorageService(sp.GetRequiredService<MaterialLedgerDbContext>(), _root));
            if (_multipartBodyLengthLimit is not null)
                services.PostConfigure<Microsoft.AspNetCore.Http.Features.FormOptions>(options => options.MultipartBodyLengthLimit = _multipartBodyLengthLimit.Value);
            services.AddAuthentication(o => { o.DefaultAuthenticateScheme = TestAuth.TestScheme; o.DefaultChallengeScheme = TestAuth.TestScheme; o.DefaultForbidScheme = TestAuth.TestScheme; }).AddScheme<AuthenticationSchemeOptions, TestAuth>(TestAuth.TestScheme, _ => { });
        });
        }

        public async Task SeedPurchaseOrderAsync() { using var scope = Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MaterialLedgerDbContext>(); db.PurchaseOrders.Add(new PurchaseOrder { PurchaseOrderId = 41, VendorName = "Vendor", PurchaseOrderNumber = "PO-41", CreatedBy = 1, CreatedAtUtc = DateTime.UtcNow }); await db.SaveChangesAsync(); }
        public async Task<int> AttachmentCountAsync() { using var scope = Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<MaterialLedgerDbContext>().Attachments.CountAsync(); }
        public async Task DeleteStoredFileAsync(long attachmentId) { using var scope = Services.CreateScope(); var attachment = await scope.ServiceProvider.GetRequiredService<MaterialLedgerDbContext>().Attachments.SingleAsync(x => x.AttachmentId == attachmentId); File.Delete(Path.Combine(_root, attachment.StoragePath)); }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, System.Text.Encodings.Web.UrlEncoder e) : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
    { public const string TestScheme = "attachments-test"; public const string RoleHeader = "X-Test-Role"; protected override Task<AuthenticateResult> HandleAuthenticateAsync() { var role = Request.Headers[RoleHeader].ToString(); return string.IsNullOrWhiteSpace(role) ? Task.FromResult(AuthenticateResult.NoResult()) : Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, role)], TestScheme)), TestScheme))); } }
}
