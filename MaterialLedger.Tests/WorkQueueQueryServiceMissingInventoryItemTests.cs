using MaterialLedger.Data;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

public class WorkQueueQueryServiceMissingInventoryItemTests
{
    [Fact]
    public async Task GetAsync_ignores_reservations_without_an_inventory_item_in_controlled_join_regression()
    {
        // The real SQL schema has FK_MaterialReservations_InventoryItems_InventoryItemId,
        // so a corrupt reservation cannot be persisted without altering test schema constraints.
        // This isolated provider setup supplies that legacy-corruption shape to the real service
        // and verifies its production inner join excludes it from all queue projections.
        var options = new DbContextOptionsBuilder<MaterialLedgerDbContext>()
            .UseInMemoryDatabase($"work-queue-missing-item-{Guid.NewGuid():N}")
            .Options;
        await using var db = new MaterialLedgerDbContext(options);
        db.MaterialReservations.Add(new MaterialReservation
        {
            InventoryItemId = long.MaxValue,
            Status = ReservationStatus.Proposed,
            Equipment = ReservationEquipment.Laser,
            ReservedQuantity = 1,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = 1
        });
        await db.SaveChangesAsync();

        var summary = await new WorkQueueQueryService(db, new InventoryQueryService(db, freshnessThresholdDays: 90)).GetAsync();

        Assert.Equal(0, summary.ApprovalNeededCount);
        Assert.Equal(0, summary.ReadyForCuttingCount);
        Assert.Equal(0, summary.CutResultNeededCount);
        Assert.Empty(summary.RecentWork);
    }
}
