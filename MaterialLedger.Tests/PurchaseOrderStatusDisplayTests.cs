using MaterialLedger.Data.Models;
using MaterialLedger.Web.Shared;
using Xunit;

namespace MaterialLedger.Tests;

public class PurchaseOrderStatusDisplayTests
{
    private static PurchaseOrderLine Line(PurchaseOrderLineStatus status) => new() { Status = status };

    [Fact]
    public void DeriveStatus_AllLinesCancelled_ReturnsCancelled()
    {
        var status = PurchaseOrderStatusDisplay.DeriveStatus([Line(PurchaseOrderLineStatus.Cancelled), Line(PurchaseOrderLineStatus.Cancelled)]);
        Assert.Equal(PurchaseOrderStatus.Cancelled, status);
    }

    [Fact]
    public void DeriveStatus_AnyLineOpenOrPartial_ReturnsOpen()
    {
        var status = PurchaseOrderStatusDisplay.DeriveStatus([Line(PurchaseOrderLineStatus.Received), Line(PurchaseOrderLineStatus.PartiallyReceived)]);
        Assert.Equal(PurchaseOrderStatus.Open, status);
    }

    [Fact]
    public void DeriveStatus_AllLinesReceivedOrCancelledWithAtLeastOneReceived_ReturnsReceived()
    {
        var status = PurchaseOrderStatusDisplay.DeriveStatus([Line(PurchaseOrderLineStatus.Received), Line(PurchaseOrderLineStatus.Cancelled)]);
        Assert.Equal(PurchaseOrderStatus.Received, status);
    }
}
