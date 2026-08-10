using MaterialLedger.Data.Models;

namespace MaterialLedger.Web.Shared;

public enum PurchaseOrderStatus { Open, Received, Cancelled }

public static class PurchaseOrderStatusDisplay
{
    public static PurchaseOrderStatus DeriveStatus(IReadOnlyCollection<PurchaseOrderLine> lines)
    {
        if (lines.Count == 0 || lines.All(x => x.Status == PurchaseOrderLineStatus.Cancelled))
            return PurchaseOrderStatus.Cancelled;
        if (lines.Any(x => x.Status is PurchaseOrderLineStatus.Open or PurchaseOrderLineStatus.PartiallyReceived))
            return PurchaseOrderStatus.Open;
        return PurchaseOrderStatus.Received;
    }

    public static string Label(PurchaseOrderStatus status) => status switch
    {
        PurchaseOrderStatus.Open => "Open",
        PurchaseOrderStatus.Received => "Received",
        PurchaseOrderStatus.Cancelled => "Cancelled",
        _ => "Unknown"
    };
}
