using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Census;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Materials;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using MaterialLedger.Web.Authorization;
using MaterialLedger.Web.Pages.Shared;
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
    PepMaterialMappingService mappings,
    ReservationService reservations,
    OpeningBalanceService openingBalance) : PageModel
{
    [BindProperty] public long? MaterialSpecId { get; set; }
    [BindProperty] public string? MaterialGroupKey { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public long FoundSheetLocationId { get; set; }
    [BindProperty] public decimal FoundSheetWidthIn { get; set; }
    [BindProperty] public decimal FoundSheetLengthIn { get; set; }
    [BindProperty] public decimal FoundSheetQuantity { get; set; }
    [BindProperty] public string? FoundSheetNotes { get; set; }
    public PepNestDetails? Nest { get; private set; }
    public MaterialSpecExternalMapping? Mapping { get; private set; }
    public IReadOnlyList<MaterialSpec> MaterialSpecs { get; private set; } = [];
    public IReadOnlyList<PepCatalogMaterialGroup> MaterialGroups { get; private set; } = [];
    public IReadOnlyList<InventoryItemSummary> AvailableItems { get; private set; } = [];
    public IReadOnlyList<ProgramSheetRequirement> ProgramSheetRequirements { get; private set; } = [];
    public IReadOnlyList<ProgramSheetShortfall> ProgramSheetShortfalls { get; private set; } = [];
    public IReadOnlyList<MaterialReservation> ProgramReservations { get; private set; } = [];
    public IReadOnlyList<InventoryLocation> MaterialLocations { get; private set; } = [];
    public bool ShowFoundSheetDialog { get; private set; }
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

    public async Task<IActionResult> OnPostReserveProgramSheetsAsync(int year, string nestName)
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager")) return Forbid();
        await LoadAsync(year, nestName);
        if (Nest is null) return NotFound();
        if (Mapping?.MaterialSpec is null || ProgramSheetRequirements.Count == 0)
        {
            Error = "Map the PEP material and confirm its program sheet sizes before creating holds.";
            return Page();
        }

        var plan = BuildReservationPlan();
        if (plan.Shortfalls.Count > 0)
        {
            ProgramSheetShortfalls = plan.Shortfalls;
            SelectFirstShortfall();
            ShowFoundSheetDialog = true;
            Error = "No holds were created. Record a found sheet or continue the inventory search, then the program will be checked again.";
            return Page();
        }

        return await CreateReservationBatchAsync(year, nestName, plan.ItemIds);
    }

    public async Task<IActionResult> OnPostRecordFoundSheetAsync(int year, string nestName)
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager")) return Forbid();
        await LoadAsync(year, nestName);
        if (Nest is null || Mapping?.MaterialSpec is null) return NotFound();

        var plan = BuildReservationPlan();
        var shortfall = plan.Shortfalls.SingleOrDefault(x => x.WidthIn == FoundSheetWidthIn && x.LengthIn == FoundSheetLengthIn);
        var location = MaterialLocations.SingleOrDefault(x => x.InventoryLocationId == FoundSheetLocationId);
        if (shortfall is null || location is null || FoundSheetQuantity < 1 || FoundSheetQuantity != decimal.Truncate(FoundSheetQuantity) || FoundSheetQuantity > shortfall.Quantity)
        {
            ProgramSheetShortfalls = plan.Shortfalls;
            SelectFirstShortfall();
            ShowFoundSheetDialog = true;
            Error = "Choose one current shortage, an active material slot, and a whole-sheet quantity no greater than the shortage.";
            return Page();
        }

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var notes = string.IsNullOrWhiteSpace(FoundSheetNotes)
                ? $"Found while preparing PEP nest {Nest.Name}."
                : $"Found while preparing PEP nest {Nest.Name}. {FoundSheetNotes.Trim()}";
            // Each found drop remains a distinct, auditable physical item rather than being merged
            // into a bundle. This preserves the one-sheet-per-reservation rule.
            for (var count = 0; count < FoundSheetQuantity; count++)
            {
                await openingBalance.RecordOpeningBalanceAsync(Guid.NewGuid(), new OpeningBalanceRequest(
                    location.InventoryLocationId, Mapping.MaterialSpecId, InventoryItemKind.Drop, 1,
                    FoundSheetWidthIn, FoundSheetLengthIn, ShapeClassification.Rectangular, false, notes), userId);
            }

            await LoadAsync(year, nestName);
            var refreshedPlan = BuildReservationPlan();
            if (refreshedPlan.Shortfalls.Count > 0)
            {
                ProgramSheetShortfalls = refreshedPlan.Shortfalls;
                SelectFirstShortfall();
                ShowFoundSheetDialog = true;
                Error = "The found sheet was recorded, but other program sheet sizes are still short. No holds were created.";
                return Page();
            }
            return await CreateReservationBatchAsync(year, nestName, refreshedPlan.ItemIds);
        }
        catch (InvalidOperationException ex)
        {
            Error = $"No holds were created: {ex.Message}";
            await LoadAsync(year, nestName);
            return Page();
        }
    }

    private async Task LoadAsync(int year, string nestName)
    {
        try { Nest = await pepNests.GetNestAsync(year, nestName); }
        catch (HttpRequestException) { Error = "PEP nest details could not be reached."; return; }
        if (Nest?.Material is null) return;

        ProgramSheetRequirements = Nest.Plates
            .Where(plate => plate.Size is not null && plate.Size.Width > 0 && plate.Size.Length > 0 && plate.Qty > 0)
            .GroupBy(plate => new { plate.Size!.Width, plate.Size.Length })
            .Select(group => new ProgramSheetRequirement(group.Key.Width, group.Key.Length, group.Sum(plate => plate.Qty)))
            .OrderBy(requirement => requirement.WidthIn).ThenBy(requirement => requirement.LengthIn).ToList();

        ProgramReservations = await db.MaterialReservations
            .Include(reservation => reservation.InventoryItem)!.ThenInclude(item => item!.InventoryLocation)
            .Where(reservation => reservation.PepNestYear == year
                && reservation.PepNestName == nestName
                && (reservation.Status == ReservationStatus.Proposed
                    || reservation.Status == ReservationStatus.Approved
                    || reservation.Status == ReservationStatus.AwaitingCloseout))
            .OrderByDescending(reservation => reservation.CreatedAtUtc)
            .ThenByDescending(reservation => reservation.MaterialReservationId)
            .ToListAsync();

        MaterialSpecs = await db.MaterialSpecs.Where(x => x.IsActive).OrderBy(x => x.DisplayName).ToListAsync();
        MaterialLocations = await ActiveMaterialSlotQuery.Create(db).ToListAsync();
        MaterialGroups = MaterialSpecs
            .GroupBy(spec => new { spec.MaterialFamily, spec.Grade, spec.Finish })
            .Select((group, index) => new PepCatalogMaterialGroup(
                $"material-{index}",
                string.Join(" ", new[] { group.Key.MaterialFamily, group.Key.Grade, group.Key.Finish }.Where(value => !string.IsNullOrWhiteSpace(value))),
                group.OrderBy(spec => spec.ThicknessIn).ToList()))
            .ToList();
        Mapping = await db.MaterialSpecExternalMappings
            .Include(x => x.MaterialSpec)
            .Where(x => x.ExternalSystem == "PEP" && x.ExternalKey == Nest.Material.Number.ToString() && x.Status == ExternalMappingStatus.Approved)
            .SingleOrDefaultAsync();
        MaterialSpecId ??= Mapping?.MaterialSpecId;
        MaterialGroupKey ??= MaterialGroups.SingleOrDefault(group => group.Thicknesses.Any(spec => spec.MaterialSpecId == MaterialSpecId))?.Key;

        if (Mapping?.MaterialSpec is not null)
        {
            var spec = Mapping.MaterialSpec;
            AvailableItems = await inventory.SearchAsync(new InventoryFilter(
                spec.MaterialFamily, spec.Grade, spec.Finish, spec.ThicknessIn,
                null, null, null, null, AvailableOnly: true));
        }
    }

    private ReservationPlan BuildReservationPlan()
    {
        var itemIds = new List<long>();
        var shortfalls = new List<ProgramSheetShortfall>();
        foreach (var requirement in ProgramSheetRequirements)
        {
            var candidates = AvailableItems.Where(item => item.UsableWidthIn == requirement.WidthIn && item.UsableLengthIn == requirement.LengthIn)
                .OrderBy(item => item.LocationCode).ThenBy(item => item.PublicItemCode).ToList();
            var remaining = requirement.Quantity;
            foreach (var item in candidates)
            {
                var take = Math.Min(remaining, decimal.ToInt32(item.AvailableQuantity));
                itemIds.AddRange(Enumerable.Repeat(item.InventoryItemId, take));
                remaining -= take;
                if (remaining == 0) break;
            }
            if (remaining > 0)
                shortfalls.Add(new ProgramSheetShortfall(requirement.WidthIn, requirement.LengthIn, remaining));
        }
        return new ReservationPlan(itemIds, shortfalls);
    }

    private void SelectFirstShortfall()
    {
        var first = ProgramSheetShortfalls.FirstOrDefault();
        if (first is null) return;
        FoundSheetWidthIn = first.WidthIn;
        FoundSheetLengthIn = first.LengthIn;
        FoundSheetQuantity = first.Quantity;
    }

    private async Task<IActionResult> CreateReservationBatchAsync(int year, string nestName, IReadOnlyList<long> itemIds)
    {
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var requests = itemIds.Select(id => new CreateReservationRequest(id, null, null, null, null, year, Nest!.Name, null, null, null, null)).ToList();
            var result = await reservations.CreateReservationBatchAsync(Guid.NewGuid(), requests, userId);
            TempData["Message"] = $"Created {result.Count} proposed material hold(s) for PEP nest {Nest!.Name}.";
            return RedirectToPage(new { year, nestName });
        }
        catch (InvalidOperationException ex)
        {
            Error = $"No holds were created: {ex.Message}";
            await LoadAsync(year, nestName);
            return Page();
        }
    }
}

public record ProgramSheetRequirement(decimal WidthIn, decimal LengthIn, int Quantity);
public record ProgramSheetShortfall(decimal WidthIn, decimal LengthIn, int Quantity);
public record ReservationPlan(IReadOnlyList<long> ItemIds, IReadOnlyList<ProgramSheetShortfall> Shortfalls);
public record PepCatalogMaterialGroup(string Key, string DisplayName, IReadOnlyList<MaterialSpec> Thicknesses);
