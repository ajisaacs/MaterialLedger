using MaterialLedger.Data.Models;

namespace MaterialLedger.Web.Shared;

public sealed record ReservationWorkflowDisplayValue(string Label, string CssClass, string Description);

public static class ReservationWorkflowDisplay
{
    public static ReservationWorkflowDisplayValue For(ReservationStatus status) => status switch
    {
        ReservationStatus.Proposed => new("Held — approval needed", "pending", "A manager must approve this hold before material is ready for cutting."),
        ReservationStatus.Approved => new("Ready for cutting", "ready", "The held material is approved and ready for the assigned work."),
        ReservationStatus.Released => new("Hold released", "complete", "This material is no longer held for the work reference."),
        ReservationStatus.AwaitingCloseout => new("Cut finished — record result", "action-needed", "Record what remains after the cut so the ledger reflects the physical material."),
        ReservationStatus.ClosedConsumed => new("Closed — material consumed", "complete", "The cut result was recorded as consumed material."),
        ReservationStatus.ClosedReturned => new("Closed — sheet returned", "complete", "The full sheet was returned to inventory."),
        ReservationStatus.Cancelled => new("Hold cancelled", "complete", "This hold was cancelled before a cut result was recorded."),
        ReservationStatus.Exception => new("Material needs review", "attention", "The material outcome needs review before it can be treated as available."),
        _ => new("Workflow status unavailable", "unknown", "The stored workflow status is not recognized by this application version.")
    };
}
