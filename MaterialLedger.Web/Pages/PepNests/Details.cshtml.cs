using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Materials;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.PepNests;

[Authorize]
public class DetailsModel(
    PepNestQueryService pepNests,
    MaterialLedgerDbContext db,
    InventoryQueryService inventory,
    PepMaterialMappingService mappings) : PageModel
{
    [BindProperty] public long? MaterialSpecId { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    public PepNestDetails? Nest { get; private set; }
    public MaterialSpecExternalMapping? Mapping { get; private set; }
    public IReadOnlyList<MaterialSpec> MaterialSpecs { get; private set; } = [];
    public IReadOnlyList<InventoryItemSummary> AvailableItems { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(int year, string nestName)
    {
        await LoadAsync(year, nestName);
        return Nest is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostMapAsync(int year, string nestName)
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager")) return Forbid();
        Nest = await pepNests.GetNestAsync(year, nestName);
        if (Nest?.Material is null) return NotFound();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await mappings.MapAsync(CorrelationId, new MapPepMaterialRequest(Nest.Material.Number, MaterialSpecId ?? 0), userId);
            return RedirectToPage(new { year, nestName });
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            await LoadAsync(year, nestName);
            return Page();
        }
    }

    private async Task LoadAsync(int year, string nestName)
    {
        try { Nest = await pepNests.GetNestAsync(year, nestName); }
        catch (HttpRequestException) { Error = "PEP nest details could not be reached."; return; }
        if (Nest?.Material is null) return;

        MaterialSpecs = await db.MaterialSpecs.Where(x => x.IsActive).OrderBy(x => x.DisplayName).ToListAsync();
        Mapping = await db.MaterialSpecExternalMappings
            .Include(x => x.MaterialSpec)
            .Where(x => x.ExternalSystem == "PEP" && x.ExternalKey == Nest.Material.Number.ToString() && x.Status == ExternalMappingStatus.Approved)
            .SingleOrDefaultAsync();
        MaterialSpecId ??= Mapping?.MaterialSpecId;

        if (Mapping?.MaterialSpec is not null)
        {
            var spec = Mapping.MaterialSpec;
            AvailableItems = await inventory.SearchAsync(new InventoryFilter(
                spec.MaterialFamily, spec.Grade, spec.Finish, spec.ThicknessIn,
                null, null, null, null, AvailableOnly: true));
        }
    }
}
