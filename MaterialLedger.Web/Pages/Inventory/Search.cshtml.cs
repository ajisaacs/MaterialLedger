using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Inventory;

[Authorize]
public class SearchModel(InventoryQueryService inventoryQuery) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? MaterialFamily { get; set; }
    [BindProperty(SupportsGet = true)] public string? Grade { get; set; }
    [BindProperty(SupportsGet = true)] public string? Finish { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? ThicknessIn { get; set; }
    [BindProperty(SupportsGet = true)] public InventoryItemKind? ItemKind { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinUsableWidthIn { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinUsableLengthIn { get; set; }
    [BindProperty(SupportsGet = true)] public string? LocationCode { get; set; }
    [BindProperty(SupportsGet = true)] public bool AvailableOnly { get; set; } = true;

    [BindProperty(SupportsGet = true)] public bool FilterMaterialFamilies { get; set; }
    [BindProperty(SupportsGet = true)] public List<string> MaterialFamilies { get; set; } = [];
    [BindProperty(SupportsGet = true)] public bool FilterGrades { get; set; }
    [BindProperty(SupportsGet = true)] public List<string> Grades { get; set; } = [];
    [BindProperty(SupportsGet = true)] public bool FilterFinishes { get; set; }
    [BindProperty(SupportsGet = true)] public List<string> Finishes { get; set; } = [];
    [BindProperty(SupportsGet = true)] public bool FilterThicknesses { get; set; }
    [BindProperty(SupportsGet = true)] public List<decimal> ThicknessesIn { get; set; } = [];
    [BindProperty(SupportsGet = true)] public bool FilterItemKinds { get; set; }
    [BindProperty(SupportsGet = true)] public List<InventoryItemKind> ItemKinds { get; set; } = [];
    [BindProperty(SupportsGet = true)] public bool FilterLocationCodes { get; set; }
    [BindProperty(SupportsGet = true)] public List<string> LocationCodes { get; set; } = [];

    public InventoryFilterOptions FilterOptions { get; private set; } = new([], [], [], [], [], []);
    public List<InventoryItemSummary> Results { get; set; } = [];
    public bool CanReserve { get; set; }

    public async Task OnGetAsync()
    {
        CanReserve = User.IsInRole("Programmer") || User.IsInRole("Manager");
        NormalizeLegacySingleValueFilters();
        FilterOptions = await inventoryQuery.GetFilterOptionsAsync();
        var filter = new InventoryFilter(MaterialFamily, Grade, Finish, ThicknessIn, ItemKind,
            MinUsableWidthIn, MinUsableLengthIn, LocationCode, AvailableOnly,
            FilterMaterialFamilies ? MaterialFamilies : null,
            FilterGrades ? Grades : null,
            FilterFinishes ? Finishes : null,
            FilterThicknesses ? ThicknessesIn : null,
            FilterItemKinds ? ItemKinds : null,
            FilterLocationCodes ? LocationCodes : null);
        Results = (await inventoryQuery.SearchAsync(filter)).ToList();
    }

    private void NormalizeLegacySingleValueFilters()
    {
        if (!string.IsNullOrWhiteSpace(MaterialFamily) && !FilterMaterialFamilies) { FilterMaterialFamilies = true; MaterialFamilies = [MaterialFamily]; }
        if (!string.IsNullOrWhiteSpace(Grade) && !FilterGrades) { FilterGrades = true; Grades = [Grade]; }
        if (!string.IsNullOrWhiteSpace(Finish) && !FilterFinishes) { FilterFinishes = true; Finishes = [Finish]; }
        if (ThicknessIn is not null && !FilterThicknesses) { FilterThicknesses = true; ThicknessesIn = [ThicknessIn.Value]; }
        if (ItemKind is not null && !FilterItemKinds) { FilterItemKinds = true; ItemKinds = [ItemKind.Value]; }
        if (!string.IsNullOrWhiteSpace(LocationCode) && !FilterLocationCodes) { FilterLocationCodes = true; LocationCodes = [LocationCode]; }
    }
}
