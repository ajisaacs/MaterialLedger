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

    public List<InventoryItemSummary> Results { get; set; } = [];
    public bool CanReserve { get; set; }

    public async Task OnGetAsync()
    {
        CanReserve = User.IsInRole("Programmer") || User.IsInRole("Manager");
        var filter = new InventoryFilter(MaterialFamily, Grade, Finish, ThicknessIn, ItemKind,
            MinUsableWidthIn, MinUsableLengthIn, LocationCode, AvailableOnly);
        Results = (await inventoryQuery.SearchAsync(filter)).ToList();
    }
}
