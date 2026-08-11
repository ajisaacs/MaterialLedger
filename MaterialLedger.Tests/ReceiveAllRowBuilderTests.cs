using MaterialLedger.Data.Models;
using MaterialLedger.Web.Pages.PurchaseOrders;
using Xunit;

namespace MaterialLedger.Tests;

public class ReceiveAllRowBuilderTests
{
    private static MaterialSpec Spec(long id = 1) => new() { MaterialSpecId = id, MaterialFamily = "Steel", DisplayName = "Steel Plate", ThicknessIn = 0.25m, IsActive = true };

    private static InventoryLocation Location(long id, string code) => new()
    {
        InventoryLocationId = id,
        LocationCode = code,
        DisplayName = code,
        LocationType = InventoryLocationType.Shelf,
        StorageMode = InventoryStorageMode.RolloutShelf,
        IsActive = true
    };

    private static PurchaseOrderLineAllocation Allocation(long id, long lineId, InventoryLocation location, decimal quantity) => new()
    {
        PurchaseOrderLineAllocationId = id,
        PurchaseOrderLineId = lineId,
        InventoryLocationId = location.InventoryLocationId,
        InventoryLocation = location,
        QuantityPlanned = quantity,
        CreatedAtUtc = DateTime.UtcNow,
        CreatedBy = 1
    };

    [Fact]
    public void BuildRows_AllocationBackedRow_PrefillsFromAllocation()
    {
        var location = Location(10, "S-1");
        var line = new PurchaseOrderLine
        {
            PurchaseOrderLineId = 100,
            LineNumber = 1,
            MaterialSpecId = 1,
            MaterialSpec = Spec(),
            QuantityOrdered = 20m,
            QuantityReceived = 0m,
            UnitPrice = 5.5m,
            Status = PurchaseOrderLineStatus.Open,
            LocationAllocations = [Allocation(1, 100, location, 20m)]
        };
        var order = new PurchaseOrder { PurchaseOrderId = 1, Lines = [line] };

        var rows = ReceiveAllModel.BuildRows(order);

        var row = Assert.Single(rows);
        Assert.True(row.Selected);
        Assert.Equal(1L, row.AllocationId);
        Assert.Equal(location.InventoryLocationId, row.LocationId);
        Assert.Equal("S-1", row.LocationDisplay);
        Assert.Equal(20m, row.Quantity);
        Assert.Equal(5.5m, row.CostPerUnit);
    }

    [Fact]
    public void BuildRows_UnallocatedRemainder_AddsUnselectedRow()
    {
        var line = new PurchaseOrderLine
        {
            PurchaseOrderLineId = 100,
            LineNumber = 1,
            MaterialSpecId = 1,
            MaterialSpec = Spec(),
            QuantityOrdered = 20m,
            QuantityReceived = 0m,
            UnitPrice = 3m,
            Status = PurchaseOrderLineStatus.Open,
            LocationAllocations = []
        };
        var order = new PurchaseOrder { PurchaseOrderId = 1, Lines = [line] };

        var rows = ReceiveAllModel.BuildRows(order);

        var row = Assert.Single(rows);
        Assert.False(row.Selected);
        Assert.Null(row.AllocationId);
        Assert.Null(row.LocationId);
        Assert.Equal(20m, row.Quantity);
        Assert.Equal(3m, row.CostPerUnit);
    }

    [Fact]
    public void BuildRows_PartialAllocation_ProducesAllocationRowAndRemainderRow()
    {
        var location = Location(10, "S-1");
        var line = new PurchaseOrderLine
        {
            PurchaseOrderLineId = 100,
            LineNumber = 1,
            MaterialSpecId = 1,
            MaterialSpec = Spec(),
            QuantityOrdered = 20m,
            QuantityReceived = 0m,
            UnitPrice = 3m,
            Status = PurchaseOrderLineStatus.Open,
            LocationAllocations = [Allocation(1, 100, location, 12m)]
        };
        var order = new PurchaseOrder { PurchaseOrderId = 1, Lines = [line] };

        var rows = ReceiveAllModel.BuildRows(order);

        Assert.Equal(2, rows.Count);
        var allocationRow = rows[0];
        Assert.True(allocationRow.Selected);
        Assert.Equal(1L, allocationRow.AllocationId);
        Assert.Equal(12m, allocationRow.Quantity);

        var remainderRow = rows[1];
        Assert.False(remainderRow.Selected);
        Assert.Null(remainderRow.AllocationId);
        Assert.Equal(8m, remainderRow.Quantity);
    }

    [Fact]
    public void BuildRows_FullyAllocatedLine_NoRemainderRow()
    {
        var location = Location(10, "S-1");
        var line = new PurchaseOrderLine
        {
            PurchaseOrderLineId = 100,
            LineNumber = 1,
            MaterialSpecId = 1,
            MaterialSpec = Spec(),
            QuantityOrdered = 20m,
            QuantityReceived = 0m,
            UnitPrice = 3m,
            Status = PurchaseOrderLineStatus.Open,
            LocationAllocations = [Allocation(1, 100, location, 20m)]
        };
        var order = new PurchaseOrder { PurchaseOrderId = 1, Lines = [line] };

        var rows = ReceiveAllModel.BuildRows(order);

        var row = Assert.Single(rows);
        Assert.Equal(1L, row.AllocationId);
    }

    [Fact]
    public void BuildRows_FullyAllocatedLine_AccountsForAlreadyReceivedQuantity()
    {
        var location = Location(10, "S-1");
        var line = new PurchaseOrderLine
        {
            PurchaseOrderLineId = 100,
            LineNumber = 1,
            MaterialSpecId = 1,
            MaterialSpec = Spec(),
            QuantityOrdered = 20m,
            QuantityReceived = 5m,
            UnitPrice = 3m,
            Status = PurchaseOrderLineStatus.PartiallyReceived,
            LocationAllocations = [Allocation(1, 100, location, 15m)]
        };
        var order = new PurchaseOrder { PurchaseOrderId = 1, Lines = [line] };

        var rows = ReceiveAllModel.BuildRows(order);

        var row = Assert.Single(rows);
        Assert.Equal(1L, row.AllocationId);
    }

    [Theory]
    [InlineData(PurchaseOrderLineStatus.Cancelled)]
    [InlineData(PurchaseOrderLineStatus.Received)]
    public void BuildRows_CancelledOrReceivedLine_ContributesNoRows(PurchaseOrderLineStatus status)
    {
        var location = Location(10, "S-1");
        var line = new PurchaseOrderLine
        {
            PurchaseOrderLineId = 100,
            LineNumber = 1,
            MaterialSpecId = 1,
            MaterialSpec = Spec(),
            QuantityOrdered = 20m,
            QuantityReceived = status == PurchaseOrderLineStatus.Received ? 20m : 0m,
            UnitPrice = 3m,
            Status = status,
            LocationAllocations = [Allocation(1, 100, location, 5m)]
        };
        var order = new PurchaseOrder { PurchaseOrderId = 1, Lines = [line] };

        var rows = ReceiveAllModel.BuildRows(order);

        Assert.Empty(rows);
    }

    [Fact]
    public void BuildRows_MultipleLines_OrderedByLineNumber()
    {
        var lineB = new PurchaseOrderLine
        {
            PurchaseOrderLineId = 200,
            LineNumber = 2,
            MaterialSpecId = 2,
            MaterialSpec = Spec(2),
            QuantityOrdered = 10m,
            QuantityReceived = 0m,
            UnitPrice = 7m,
            Status = PurchaseOrderLineStatus.Open,
            LocationAllocations = []
        };
        var lineA = new PurchaseOrderLine
        {
            PurchaseOrderLineId = 100,
            LineNumber = 1,
            MaterialSpecId = 1,
            MaterialSpec = Spec(1),
            QuantityOrdered = 10m,
            QuantityReceived = 0m,
            UnitPrice = 4m,
            Status = PurchaseOrderLineStatus.Open,
            LocationAllocations = []
        };
        // Insert in reverse order to prove BuildRows sorts, not just preserves input order.
        var order = new PurchaseOrder { PurchaseOrderId = 1, Lines = [lineB, lineA] };

        var rows = ReceiveAllModel.BuildRows(order);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].LineNumber);
        Assert.Equal(2, rows[1].LineNumber);
    }

    [Fact]
    public void BuildRows_CostPerUnit_DefaultsFromLineUnitPrice()
    {
        var location = Location(10, "S-1");
        var line = new PurchaseOrderLine
        {
            PurchaseOrderLineId = 100,
            LineNumber = 1,
            MaterialSpecId = 1,
            MaterialSpec = Spec(),
            QuantityOrdered = 20m,
            QuantityReceived = 0m,
            UnitPrice = 9.75m,
            Status = PurchaseOrderLineStatus.Open,
            LocationAllocations = [Allocation(1, 100, location, 12m)]
        };
        var order = new PurchaseOrder { PurchaseOrderId = 1, Lines = [line] };

        var rows = ReceiveAllModel.BuildRows(order);

        Assert.All(rows, row => Assert.Equal(9.75m, row.CostPerUnit));
    }
}
