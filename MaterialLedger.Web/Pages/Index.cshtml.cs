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
    public string? NewSheetError { get; private set; }
    public bool ShowAddSheetDialog { get; private set; }

    public async Task OnGetAsync()
    {
        Summary = await workQueue.GetAsync();
        await LoadNewSheetOptionsAsync();
    }

    public async Task<IActionResult> OnPostAddSheetAsync()
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager"))
            return Forbid();

        Summary = await workQueue.GetAsync();
        await LoadNewSheetOptionsAsync();
        ShowAddSheetDialog = true;

        if (MaterialLocations.All(x => x.InventoryLocationId != NewSheet.LocationId))
        {
            NewSheetError = "Choose an active material slot.";
            return Page();
        }

        if (NewSheet.ExistingMaterialSpecId is null)
        {
            NewSheetError = "Choose a material and thickness from the catalog.";
            return Page();
        }

        if (AvailableSpecs.All(x => x.MaterialSpecId != NewSheet.ExistingMaterialSpecId))
        {
            NewSheetError = "The selected material is no longer available. Please choose another.";
            return Page();
        }

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var result = await openingBalance.RecordOpeningBalanceAsync(
                NewSheet.CorrelationId,
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

            return RedirectToPage("/Census/Success", new { code = result.PublicItemCode });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or MaterialLedger.Data.Idempotency.OperationConflictException)
        {
            NewSheetError = ex is MaterialLedger.Data.Idempotency.OperationConflictException
                ? "This form was already submitted with different values — please review and resubmit."
                : ex.Message;
            return Page();
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
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public long? ExistingMaterialSpecId { get; set; }
    public decimal? UsableWidthIn { get; set; }
    public decimal? UsableLengthIn { get; set; }
    public bool IsQuarantined { get; set; }
    public string? Notes { get; set; }
}
