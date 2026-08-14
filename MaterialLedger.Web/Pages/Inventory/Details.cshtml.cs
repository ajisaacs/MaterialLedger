using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Inventory;

[Authorize]
public class DetailsModel(InventoryQueryService inventoryQuery) : PageModel
{
    public InventoryItemSummary? Item { get; set; }
    public InventoryItemSummary? SourceSheet { get; set; }
    public IReadOnlyList<ReservationSummary> Reservations { get; set; } = [];
    public IReadOnlyList<TransactionSummary> Transactions { get; set; } = [];
    public IReadOnlyList<ExceptionSummary> Exceptions { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(long id)
    {
        Item = await inventoryQuery.GetItemSummaryAsync(id);
        if (Item is null)
            return NotFound();

        if (Item.ItemKind == InventoryItemKind.Drop)
            SourceSheet = await inventoryQuery.GetSourceSheetAsync(id);

        // InventoryQueryService is scoped and shares the request DbContext. EF Core contexts
        // permit only one active operation, so these reads must remain sequential.
        Reservations = await inventoryQuery.GetItemReservationsAsync(id);
        Transactions = await inventoryQuery.GetItemTransactionsAsync(id);
        Exceptions = await inventoryQuery.GetItemExceptionsAsync(id);

        return Page();
    }
}
