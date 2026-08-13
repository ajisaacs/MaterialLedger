using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace MaterialLedger.Web.Attachments;

public sealed class AttachmentUploadOptions
{
    public long MaxAttachmentBytes { get; init; } = 10 * 1024 * 1024;
    public string[] AllowedAttachmentMediaTypes { get; init; } = [];
    public string[] AllowedAttachmentExtensions { get; init; } = [];
}

public enum AttachmentUploadValidationResult
{
    Valid,
    Empty,
    TooLarge,
    UnsupportedMediaType
}

public static class AttachmentUploadLimits
{
    // Multipart boundaries and headers count toward FormOptions' body limit, not the file policy.
    public const long MultipartOverheadAllowanceBytes = 64 * 1024;

    public static long MultipartBodyLengthLimit(long maxFileBytes) =>
        maxFileBytes > long.MaxValue - MultipartOverheadAllowanceBytes
            ? long.MaxValue
            : maxFileBytes + MultipartOverheadAllowanceBytes;
}

public sealed class AttachmentUploadPolicy(IOptions<AttachmentUploadOptions> options)
{
    private readonly AttachmentUploadOptions _options = options.Value;

    public AttachmentUploadValidationResult Validate(IFormFile? file)
    {
        if (file is null || file.Length == 0) return AttachmentUploadValidationResult.Empty;
        if (file.Length > _options.MaxAttachmentBytes) return AttachmentUploadValidationResult.TooLarge;

        var extension = Path.GetExtension(Path.GetFileName(file.FileName));
        return _options.AllowedAttachmentMediaTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase)
            && _options.AllowedAttachmentExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? AttachmentUploadValidationResult.Valid
            : AttachmentUploadValidationResult.UnsupportedMediaType;
    }
}
