namespace MaterialLedger.Web.Api.Contracts;

public sealed record AttachmentContract(long AttachmentId, string FileName, string ContentType, long ByteLength, string Checksum, DateTime UploadedAtUtc, string DownloadUrl);
