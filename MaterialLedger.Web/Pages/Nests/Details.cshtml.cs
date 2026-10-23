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

namespace MaterialLedger.Web.Pages.Nests;

[Authorize]
public class DetailsModel(
    OpenNestClient openNest,
    MaterialLedgerDbContext db,
    InventoryQueryService inventory,
    OpenNestMaterialMappingService mappings,
    ReservationService reservations,
    OpeningBalanceService openingBalance) : PageModel
{
    private const string UnreachableError = "OpenNest nests could not be reached.";

    [BindProperty] public long? MaterialSpecId { get; set; }
    [BindProperty] public string? MaterialGroupKey { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public long FoundSheetLocationId { get; set; }
    [BindProperty] public decimal FoundSheetWidthIn { get; set; }
    [BindProperty] public decimal FoundSheetLengthIn { get; set; }
    [BindProperty] public decimal FoundSheetQuantity { get; set; }
    [BindProperty] public string? FoundSheetNotes { get; set; }
    public OpenNestRecord? Nest { get; private set; }
    public IReadOnlyList<OpenNestPartCount> Parts { get; private set; } = [];
    // OpenNest.Server answers 404 for /sheets until that endpoint is deployed; the nest itself still shows.
    public bool SheetsUnavailable { get; private set; }
    public bool AlreadyCut => string.Equals(Nest?.Status, "hasBeenCut", StringComparison.OrdinalIgnoreCase);
    public MaterialSpecExternalMapping? Mapping { get; private set; }
    public IReadOnlyList<MaterialSpec> MaterialSpecs { get; private set; } = [];
    public IReadOnlyList<CatalogMaterialGroup> MaterialGroups { get; private set; } = [];
    public IReadOnlyList<InventoryItemSummary> AvailableItems { get; private set; } = [];
    public IReadOnlyList<ProgramSheetRequirement> ProgramSheetRequirements { get; private set; } = [];
    public IReadOnlyList<ProgramSheetShortfall> ProgramSheetShortfalls { get; private set; } = [];
    public IReadOnlyList<MaterialReservation> ProgramReservations { get; private set; } = [];
    public IReadOnlyList<InventoryLocation> MaterialLocations { get; private set; } = [];
    public bool ShowFoundSheetDialog { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        await LoadAsync(id);
        return Nest is null && Error is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostMapAsync(Guid id)
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager")) return Forbid();
        try { Nest = await openNest.GetNestAsync(id, HttpContext.RequestAborted); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Error = UnreachableError; return Page(); }
        if (Nest is null) return NotFound();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await mappings.MapAsync(CorrelationId, new MapOpenNestMaterialRequest(Nest.Material ?? "", Nest.MaterialGrade ?? "", Nest.Thickness, MaterialSpecId ?? 0), userId);
            return RedirectToPage(new { id });
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            await LoadAsync(id);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostReserveProgramSheetsAsync(Guid id)
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager")) return Forbid();
        await LoadAsync(id);
        if (Nest is null) return Error is null ? NotFound() : Page();
        if (SheetsUnavailable)
        {
            Error = "OpenNest did not return sheet sizes for this nest, so no holds can be created yet.";
            return Page();
        }
        if (Mapping?.MaterialSpec is null || ProgramSheetRequirements.Count == 0)
        {
            Error = "Map the OpenNest material and confirm its program sheet sizes before creating holds.";
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

        return await CreateReservationBatchAsync(id, plan.ItemIds);
    }

    public async Task<IActionResult> OnPostRecordFoundSheetAsync(Guid id)
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager")) return Forbid();
        await LoadAsync(id);
        if (Nest is null && Error is not null) return Page();
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
                ? $"Found while preparing nest {Nest.Name}."
                : $"Found while preparing nest {Nest.Name}. {FoundSheetNotes.Trim()}";
            // Each found drop remains a distinct, auditable physical item rather than being merged
            // into a bundle. This preserves the one-sheet-per-reservation rule.
            for (var count = 0; count < FoundSheetQuantity; count++)
            {
                await openingBalance.RecordOpeningBalanceAsync(Guid.NewGuid(), new OpeningBalanceRequest(
                    location.InventoryLocationId, Mapping.MaterialSpecId, InventoryItemKind.Drop, 1,
                    FoundSheetWidthIn, FoundSheetLengthIn, ShapeClassification.Rectangular, false, notes), userId);
            }

            await LoadAsync(id);
            var refreshedPlan = BuildReservationPlan();
            if (refreshedPlan.Shortfalls.Count > 0)
            {
                ProgramSheetShortfalls = refreshedPlan.Shortfalls;
                SelectFirstShortfall();
                ShowFoundSheetDialog = true;
                Error = "The found sheet was recorded, but other program sheet sizes are still short. No holds were created.";
                return Page();
            }
            return await CreateReservationBatchAsync(id, refreshedPlan.ItemIds);
        }
        catch (InvalidOperationException ex)
        {
            Error = $"No holds were created: {ex.Message}";
            await LoadAsync(id);
            return Page();
        }
    }

    /// <summary>One hold per sheet, each pointing at the OpenNest nest by id with its name as a display snapshot.</summary>
    public static IReadOnlyList<CreateReservationRequest> BuildHoldRequests(Guid nestId, string? nestName, IReadOnlyList<long> itemIds) =>
        itemIds.Select(itemId => new CreateReservationRequest(itemId, null, null, null, null, null, null, null, null,
            OpenNestNestId: nestId, NestName: nestName)).ToList();

    private async Task LoadAsync(Guid id)
    {
        OpenNestSheets? sheets;
        try
        {
            Nest = await openNest.GetNestAsync(id, HttpContext.RequestAborted);
            if (Nest is null) return;
            sheets = await openNest.GetSheetsAsync(id, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Nest = null;
            Error = UnreachableError;
            return;
        }

        SheetsUnavailable = sheets is null;
        Parts = sheets?.Parts ?? [];
        ProgramSheetRequirements = (sheets?.Sheets ?? [])
            .Where(sheet => sheet.Width > 0 && sheet.Length > 0 && sheet.Quantity > 0)
            .GroupBy(sheet => new { sheet.Width, sheet.Length })
            .Select(group => new ProgramSheetRequirement(group.Key.Width, group.Key.Length, group.Sum(sheet => sheet.Quantity)))
            .OrderBy(requirement => requirement.WidthIn).ThenBy(requirement => requirement.LengthIn).ToList();

        ProgramReservations = await db.MaterialReservations
            .Include(reservation => reservation.InventoryItem)!.ThenInclude(item => item!.InventoryLocation)
            .Where(reservation => reservation.OpenNestNestId == id
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
            .Select((group, index) => new CatalogMaterialGroup(
                $"material-{index}",
                string.Join(" ", new[] { group.Key.MaterialFamily, group.Key.Grade, group.Key.Finish }.Where(value => !string.IsNullOrWhiteSpace(value))),
                group.OrderBy(spec => spec.ThicknessIn).ToList()))
            .ToList();
        var materialKey = MaterialKey(Nest);
        Mapping = materialKey is null ? null : await db.MaterialSpecExternalMappings
            .Include(x => x.MaterialSpec)
            .Where(x => x.ExternalSystem == "OpenNest" && x.ExternalKey == materialKey && x.Status == ExternalMappingStatus.Approved)
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

    // Null when OpenNest has no material or thickness to key a mapping on.
    private static string? MaterialKey(OpenNestRecord nest) =>
        string.IsNullOrWhiteSpace(nest.Material) || nest.Thickness <= 0
            ? null
            : OpenNestMaterialKey.For(nest.Material, nest.MaterialGrade, nest.Thickness);

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

    private async Task<IActionResult> CreateReservationBatchAsync(Guid id, IReadOnlyList<long> itemIds)
    {
        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var requests = BuildHoldRequests(id, Nest!.Name, itemIds);
            var result = await reservations.CreateReservationBatchAsync(Guid.NewGuid(), requests, userId);
            TempData["Message"] = $"Created {result.Count} proposed material hold(s) for nest {Nest!.Name}.";
            return RedirectToPage(new { id });
        }
        catch (InvalidOperationException ex)
        {
            Error = $"No holds were created: {ex.Message}";
            await LoadAsync(id);
            return Page();
        }
    }
}

public record ProgramSheetRequirement(decimal WidthIn, decimal LengthIn, int Quantity);
public record ProgramSheetShortfall(decimal WidthIn, decimal LengthIn, int Quantity);
public record ReservationPlan(IReadOnlyList<long> ItemIds, IReadOnlyList<ProgramSheetShortfall> Shortfalls);
public record CatalogMaterialGroup(string Key, string DisplayName, IReadOnlyList<MaterialSpec> Thicknesses);
