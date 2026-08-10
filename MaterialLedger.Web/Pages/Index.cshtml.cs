using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Census;
using MaterialLedger.Data.Materials;
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
    OpeningBalanceService openingBalance,
    MaterialSpecService materialSpecs) : PageModel
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

        if (NewSheet.ExistingMaterialSpecId is null
            && (string.IsNullOrWhiteSpace(NewSheet.MaterialFamily) || NewSheet.ThicknessIn <= 0))
        {
            NewSheetError = "Select an existing material or enter a family and thickness for a new one.";
            return Page();
        }

        if (NewSheet.ExistingMaterialSpecId is not null
            && AvailableSpecs.All(x => x.MaterialSpecId != NewSheet.ExistingMaterialSpecId))
        {
            NewSheetError = "The selected existing material is no longer available. Please choose another.";
            return Page();
        }

        try
        {
            var materialSpecId = NewSheet.ExistingMaterialSpecId
                ?? (await materialSpecs.GetOrCreateAsync(
                    NewSheet.MaterialFamily,
                    NewSheet.Grade,
                    NewSheet.Finish,
                    NewSheet.ThicknessIn,
                    $"{NewSheet.MaterialFamily} {NewSheet.Grade} {NewSheet.ThicknessIn}in".Trim())).MaterialSpecId;
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var result = await openingBalance.RecordOpeningBalanceAsync(
                NewSheet.CorrelationId,
                new OpeningBalanceRequest(
                    NewSheet.LocationId,
                    materialSpecId,
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
        AvailableSpecs = (await materialSpecs.SearchAsync(null)).ToList();
    }
}

public class NewSheetInput
{
    public long LocationId { get; set; }
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public long? ExistingMaterialSpecId { get; set; }
    public string MaterialFamily { get; set; } = string.Empty;
    public string? Grade { get; set; }
    public string? Finish { get; set; }
    public decimal ThicknessIn { get; set; }
    public decimal? UsableWidthIn { get; set; }
    public decimal? UsableLengthIn { get; set; }
    public bool IsQuarantined { get; set; }
    public string? Notes { get; set; }
}
