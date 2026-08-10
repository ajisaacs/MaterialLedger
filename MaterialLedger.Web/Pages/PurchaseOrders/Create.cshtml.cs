// MaterialLedger.Web/Pages/PurchaseOrders/Create.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.PurchaseOrders;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.PurchaseOrders;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class CreateModel(MaterialLedgerDbContext db, PurchaseOrderService purchaseOrderService) : PageModel
{
    private const int MaxLines = 20;

    [BindProperty] public string VendorName { get; set; } = string.Empty;
    [BindProperty] public string PurchaseOrderNumber { get; set; } = string.Empty;
    [BindProperty] public DateOnly? OrderDate { get; set; }
    [BindProperty] public string? Notes { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public List<PurchaseOrderLineInput> Lines { get; set; } = [];

    public List<MaterialSpec> AvailableSpecs { get; set; } = [];
    public string? Error { get; set; }

    public async Task OnGetAsync()
    {
        AvailableSpecs = await LoadAvailableSpecsAsync();
        CorrelationId = Guid.NewGuid();
        Lines = Enumerable.Range(0, MaxLines).Select(_ => new PurchaseOrderLineInput()).ToList();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        AvailableSpecs = await LoadAvailableSpecsAsync();

        var lineRequests = new List<PurchaseOrderLineRequest>();
        foreach (var line in Lines)
        {
            var isBlank = line.ExistingMaterialSpecId is null
                && line.QuantityOrdered <= 0
                && string.IsNullOrWhiteSpace(line.VendorDescription)
                && string.IsNullOrWhiteSpace(line.VendorPartNumber)
                && line.UnitPrice is null
                && line.UsableWidthIn is null
                && line.UsableLengthIn is null
                && line.QuotedDeliveryDate is null;
            if (isBlank) continue;

            if (line.ExistingMaterialSpecId is null || line.QuantityOrdered <= 0)
            {
                Error = "Choose a material and thickness and enter a quantity for every line you fill in.";
                return Page();
            }
            lineRequests.Add(new PurchaseOrderLineRequest(
                line.ExistingMaterialSpecId.Value, line.VendorDescription, line.VendorPartNumber,
                line.QuantityOrdered, line.UsableWidthIn, line.UsableLengthIn, line.UnitPrice, line.QuotedDeliveryDate));
        }

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new CreatePurchaseOrderRequest(VendorName, PurchaseOrderNumber, OrderDate, Notes, lineRequests);
            var purchaseOrderId = await purchaseOrderService.CreatePurchaseOrderAsync(CorrelationId, request, userId);
            return RedirectToPage("Details", new { id = purchaseOrderId });
        }
        catch (Exception ex) when (ex is ArgumentException or OperationConflictException)
        {
            Error = ex is OperationConflictException
                ? "This form was already submitted with different values — please review and resubmit."
                : ex.Message;
            return Page();
        }
    }

    private Task<List<MaterialSpec>> LoadAvailableSpecsAsync() => db.MaterialSpecs
        .AsNoTracking()
        .Where(x => x.IsActive)
        .OrderBy(x => x.MaterialFamily).ThenBy(x => x.Grade).ThenBy(x => x.Finish).ThenBy(x => x.ThicknessIn)
        .ToListAsync();
}

public class PurchaseOrderLineInput
{
    public long? ExistingMaterialSpecId { get; set; }
    public string? VendorDescription { get; set; }
    public string? VendorPartNumber { get; set; }
    public decimal QuantityOrdered { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? UsableWidthIn { get; set; }
    public decimal? UsableLengthIn { get; set; }
    public DateOnly? QuotedDeliveryDate { get; set; }
}
