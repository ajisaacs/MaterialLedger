// MaterialLedger.Web/Pages/PurchaseOrders/Details.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Attachments;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.PurchaseOrders;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Attachments;
using MaterialLedger.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.PurchaseOrders;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class DetailsModel(MaterialLedgerDbContext db, PurchaseOrderService purchaseOrderService, AttachmentStorageService attachmentStorage, AttachmentUploadPolicy uploadPolicy) : PageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }

    public PurchaseOrder? Order { get; set; }
    public List<Attachment> Attachments { get; set; } = [];
    public List<InventoryLocation> LeafLocations { get; set; } = [];
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return NotFound();
        CorrelationId = Guid.NewGuid();
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAttachmentAsync()
    {
        if (!await LoadAsync()) return NotFound();

        IFormFile? file;
        try
        {
            file = (await Request.ReadFormAsync()).Files.GetFile("File");
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            Error = "The uploaded file is too large.";
            return Page();
        }

        if (file is null || file.Length == 0)
        {
            Error = "Choose a file to upload.";
            return Page();
        }

        Error = uploadPolicy.Validate(file) switch
        {
            AttachmentUploadValidationResult.TooLarge => "The uploaded file is too large.",
            AttachmentUploadValidationResult.UnsupportedMediaType => "The uploaded file type is not allowed.",
            _ => null
        };
        if (Error is not null) return Page();

        await using var stream = file.OpenReadStream();
        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        await attachmentStorage.SaveAsync(AttachmentEntityType.PurchaseOrder, Id, file.FileName, file.ContentType, stream, userId);
        return RedirectToPage(new { id = Id });
    }

    public Task<IActionResult> OnPostCancelLineAsync(long lineId) =>
        ExecuteLineActionAsync(userId => purchaseOrderService.CancelLineAsync(CorrelationId, lineId, userId));

    public Task<IActionResult> OnPostCloseRemainingAsync() =>
        ExecuteLineActionAsync(userId => purchaseOrderService.CloseRemainingAsync(CorrelationId, Id, userId));

    public async Task<IActionResult> OnPostAddAllocationAsync(long lineId, long inventoryLocationId, decimal quantity)
    {
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await purchaseOrderService.AddLocationAllocationAsync(
                CorrelationId, new AddLocationAllocationRequest(lineId, inventoryLocationId, quantity), userId);
            return RedirectToPage(new { id = Id });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationConflictException)
        {
            if (!await LoadAsync()) return NotFound();
            Error = ex.Message;
            return Page();
        }
    }

    public Task<IActionResult> OnPostRemoveAllocationAsync(long allocationId) =>
        ExecuteLineActionAsync(userId => purchaseOrderService.RemoveLocationAllocationAsync(CorrelationId, allocationId, userId));

    private async Task<IActionResult> ExecuteLineActionAsync(Func<int, Task> action)
    {
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await action(userId);
            return RedirectToPage(new { id = Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationConflictException)
        {
            if (!await LoadAsync()) return NotFound();
            Error = ex.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnGetDownloadAttachmentAsync(long attachmentId)
    {
        if (!await LoadAsync()) return NotFound();

        var attachment = Attachments.FirstOrDefault(x => x.AttachmentId == attachmentId);
        if (attachment is null) return NotFound();

        var path = attachmentStorage.GetFullPath(attachment);
        if (!System.IO.File.Exists(path)) return NotFound();

        var downloadFileName = $"{Order!.PurchaseOrderNumber}-confirmation{Path.GetExtension(attachment.StoragePath)}";
        return PhysicalFile(path, attachment.ContentType ?? "application/octet-stream", downloadFileName);
    }

    private async Task<bool> LoadAsync()
    {
        Order = await db.PurchaseOrders
            .Include(x => x.Vendor)
            .Include(x => x.Lines).ThenInclude(x => x.MaterialSpec)
            .Include(x => x.Lines).ThenInclude(x => x.VendorPart)
            .Include(x => x.Lines).ThenInclude(x => x.LocationAllocations).ThenInclude(x => x.InventoryLocation)
            .FirstOrDefaultAsync(x => x.PurchaseOrderId == Id);
        if (Order is null) return false;

        LeafLocations = await ActiveMaterialSlotQuery.Create(db).ToListAsync();

        Attachments = await db.Attachments
            .Where(x => x.EntityType == AttachmentEntityType.PurchaseOrder && x.EntityId == Id)
            .OrderByDescending(x => x.UploadedAtUtc)
            .ToListAsync();
        return true;
    }
}
