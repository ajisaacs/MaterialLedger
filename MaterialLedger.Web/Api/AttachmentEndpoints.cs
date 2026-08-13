using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Attachments;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Api.Contracts;
using MaterialLedger.Web.Attachments;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Api;

public static class AttachmentEndpoints
{
    public static RouteGroupBuilder MapAttachmentEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/attachments/{entityType}/{entityId:long}", UploadAsync)
            .RequireAuthorization(MaterialLedgerPolicies.RequireProgrammerOrManager);
        api.MapGet("/attachments/{entityType}/{entityId:long}/{attachmentId:long}", DownloadAsync)
            .WithName("DownloadAttachment");
        return api;
    }

    private static async Task<IResult> UploadAsync(string entityType, long entityId, HttpRequest request, MaterialLedgerDbContext db, AttachmentStorageService storage, AttachmentUploadPolicy uploadPolicy, LinkGenerator links, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!TryParseEntityType(entityType, out var type)) return ApiProblemDetails.Validation(request.HttpContext, "entityType", "The attachment entity type is invalid.");
        if (!await ExistsAsync(db, type, entityId, cancellationToken)) return Results.NotFound();
        if (!request.HasFormContentType) return ApiProblemDetails.Validation(request.HttpContext, "file", "A multipart form file is required.");
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return ApiProblemDetails.CreateResult(request.HttpContext, "payload-too-large", StatusCodes.Status413PayloadTooLarge, "The uploaded file is too large.");
        }

        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return ApiProblemDetails.Validation(request.HttpContext, "file", "A non-empty file part is required.");
        var validation = uploadPolicy.Validate(file);
        if (validation is AttachmentUploadValidationResult.TooLarge)
            return ApiProblemDetails.CreateResult(request.HttpContext, "payload-too-large", StatusCodes.Status413PayloadTooLarge, "The uploaded file is too large.");
        if (validation is AttachmentUploadValidationResult.UnsupportedMediaType)
            return ApiProblemDetails.CreateResult(request.HttpContext, "unsupported-media-type", StatusCodes.Status415UnsupportedMediaType, "The uploaded file type is not allowed.");
        if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Results.Unauthorized();
        await using var input = file.OpenReadStream();
        var attachment = await storage.SaveAsync(type, entityId, file.FileName, file.ContentType, input, userId, cancellationToken);
        return Results.Created(links.GetPathByName("DownloadAttachment", new { entityType, entityId, attachmentId = attachment.AttachmentId }), ToContract(attachment, entityType, entityId, links));
    }

    private static async Task<IResult> DownloadAsync(string entityType, long entityId, long attachmentId, MaterialLedgerDbContext db, AttachmentStorageService storage, CancellationToken cancellationToken)
    {
        if (!TryParseEntityType(entityType, out var type)) return Results.NotFound();
        var attachment = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(x => x.AttachmentId == attachmentId && x.EntityType == type && x.EntityId == entityId, cancellationToken);
        if (attachment is null) return Results.NotFound();
        try { return Results.File(storage.OpenRead(attachment), attachment.ContentType ?? "application/octet-stream", attachment.OriginalFileName, enableRangeProcessing: true); }
        catch (FileNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException) { return Results.NotFound(); }
    }

    private static AttachmentContract ToContract(Attachment a, string entityType, long entityId, LinkGenerator links) => new(a.AttachmentId, a.OriginalFileName, a.ContentType ?? "application/octet-stream", a.ByteLength, a.Checksum ?? string.Empty, a.UploadedAtUtc, links.GetPathByName("DownloadAttachment", new { entityType, entityId, attachmentId = a.AttachmentId })!);
    private static bool TryParseEntityType(string value, out AttachmentEntityType type)
    {
        foreach (var candidate in Enum.GetValues<AttachmentEntityType>())
            if (string.Equals(value, ToRouteValue(candidate), StringComparison.OrdinalIgnoreCase)) { type = candidate; return true; }
        type = default;
        return false;
    }
    private static string ToRouteValue(AttachmentEntityType type) => System.Text.RegularExpressions.Regex.Replace(type.ToString(), "([a-z])([A-Z])", "$1-$2").ToLowerInvariant();
    private static Task<bool> ExistsAsync(MaterialLedgerDbContext db, AttachmentEntityType type, long id, CancellationToken ct) => type switch
    {
        AttachmentEntityType.InventoryItem => db.InventoryItems.AnyAsync(x => x.InventoryItemId == id, ct),
        AttachmentEntityType.MaterialReservation => db.MaterialReservations.AnyAsync(x => x.MaterialReservationId == id, ct),
        AttachmentEntityType.MaterialTransaction => db.MaterialTransactions.AnyAsync(x => x.MaterialTransactionId == id, ct),
        AttachmentEntityType.MaterialException => db.MaterialExceptions.AnyAsync(x => x.MaterialExceptionId == id, ct),
        AttachmentEntityType.PurchaseOrder => db.PurchaseOrders.AnyAsync(x => x.PurchaseOrderId == id, ct),
        _ => Task.FromResult(false)
    };
}
