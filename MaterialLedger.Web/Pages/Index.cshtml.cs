using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Census;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Pages.Shared;
using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages;

[Authorize]
public class IndexModel(
    WorkQueueQueryService workQueue,
    MaterialLedgerDbContext db,
    OpeningBalanceService openingBalance) : PageModel
{
    public WorkQueueSummary Summary { get; private set; } = new()
    {
        AvailableMaterialCount = 0,
        ApprovalNeededCount = 0,
        ReadyForCuttingCount = 0,
        CutResultNeededCount = 0,
        AttentionNeededCount = 0,
        RecentWork = []
    };

    [BindProperty] public NewSheetInput NewSheet { get; set; } = new();
    public List<InventoryLocation> MaterialLocations { get; private set; } = [];
    public List<MaterialSpec> AvailableSpecs { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Summary = await workQueue.GetAsync();
        await LoadNewSheetOptionsAsync();
    }

    public async Task<IActionResult> OnPostAddSheetAsync()
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager"))
            return Forbid();

        await LoadNewSheetOptionsAsync();

        var slot = MaterialLocations.FirstOrDefault(x => x.InventoryLocationId == NewSheet.LocationId);
        if (slot is null)
            return new JsonResult(new AddSheetResponse(false, "Choose an active material slot."));

        if (NewSheet.ExistingMaterialSpecId is null)
            return new JsonResult(new AddSheetResponse(false, "Choose a material and thickness from the catalog."));

        var spec = AvailableSpecs.FirstOrDefault(x => x.MaterialSpecId == NewSheet.ExistingMaterialSpecId);
        if (spec is null)
            return new JsonResult(new AddSheetResponse(false, "The selected material is no longer available. Please choose another."));

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var result = await openingBalance.RecordOpeningBalanceAsync(
                Guid.NewGuid(),
                new OpeningBalanceRequest(
                    NewSheet.LocationId,
                    NewSheet.ExistingMaterialSpecId.Value,
                    InventoryItemKind.FullSheet,
                    1,
                    NewSheet.UsableWidthIn,
                    NewSheet.UsableLengthIn,
                    ShapeClassification.Rectangular,
                    NewSheet.IsQuarantined,
                    NewSheet.Notes),
                userId);

            return new JsonResult(new AddSheetResponse(
                true, null, result.PublicItemCode, slot.LocationCode, spec.DisplayName,
                NewSheet.UsableWidthIn, NewSheet.UsableLengthIn));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or MaterialLedger.Data.Idempotency.OperationConflictException)
        {
            var message = ex is MaterialLedger.Data.Idempotency.OperationConflictException
                ? "This form was already submitted with different values — please review and resubmit."
                : ex.Message;
            return new JsonResult(new AddSheetResponse(false, message));
        }
    }

    private async Task LoadNewSheetOptionsAsync()
    {
        MaterialLocations = await ActiveMaterialSlotQuery.Create(db).ToListAsync();
        AvailableSpecs = await db.MaterialSpecs
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.MaterialFamily).ThenBy(x => x.Grade).ThenBy(x => x.Finish).ThenBy(x => x.ThicknessIn)
            .ToListAsync();
    }
}

public class NewSheetInput
{
    public long LocationId { get; set; }
    public long? ExistingMaterialSpecId { get; set; }
    public decimal? UsableWidthIn { get; set; }
    public decimal? UsableLengthIn { get; set; }
    public bool IsQuarantined { get; set; }
    public string? Notes { get; set; }
}

public record AddSheetResponse(
    bool Success,
    string? Error,
    string? ItemCode = null,
    string? SlotCode = null,
    string? MaterialDisplayName = null,
    decimal? UsableWidthIn = null,
    decimal? UsableLengthIn = null);
