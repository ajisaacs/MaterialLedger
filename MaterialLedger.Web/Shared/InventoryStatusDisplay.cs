// MaterialLedger.Web/Shared/InventoryStatusDisplay.cs
using MaterialLedger.Data.Models;

namespace MaterialLedger.Web.Shared;

/// <summary>
/// Shared status-to-color mapping for inventory item status badges, used by both
/// Inventory/Search.cshtml and Locations/Details.cshtml so the two pages can't drift apart.
/// </summary>
public static class InventoryStatusDisplay
{
    public static string BackgroundColor(InventoryItemStatus status) => status switch
    {
        InventoryItemStatus.Available => "#e6f4ea",
        InventoryItemStatus.Reserved => "#fff4e5",
        InventoryItemStatus.Quarantined => "#fdecea",
        InventoryItemStatus.AwaitingCloseout => "#e5e5f7",
        _ => ""
    };
}
