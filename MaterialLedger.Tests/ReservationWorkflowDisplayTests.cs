using MaterialLedger.Data.Models;
using MaterialLedger.Web.Shared;
using Xunit;

namespace MaterialLedger.Tests;

public sealed class ReservationWorkflowDisplayTests
{
    [Theory]
    [InlineData(ReservationStatus.Proposed, "Held — approval needed", "pending")]
    [InlineData(ReservationStatus.Approved, "Ready for cutting", "ready")]
    [InlineData(ReservationStatus.Released, "Hold released", "complete")]
    [InlineData(ReservationStatus.AwaitingCloseout, "Cut finished — record result", "action-needed")]
    [InlineData(ReservationStatus.ClosedConsumed, "Closed — material consumed", "complete")]
    [InlineData(ReservationStatus.ClosedReturned, "Closed — sheet returned", "complete")]
    [InlineData(ReservationStatus.Cancelled, "Hold cancelled", "complete")]
    [InlineData(ReservationStatus.Exception, "Material needs review", "attention")]
    public void Maps_every_reservation_status_to_a_plain_language_workflow_label(
        ReservationStatus status,
        string expectedLabel,
        string expectedCssClass)
    {
        var display = ReservationWorkflowDisplay.For(status);

        Assert.Equal(expectedLabel, display.Label);
        Assert.Equal(expectedCssClass, display.CssClass);
        Assert.False(string.IsNullOrWhiteSpace(display.Description));
    }
}
