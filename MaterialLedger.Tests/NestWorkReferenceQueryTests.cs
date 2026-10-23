using MaterialLedger.Data;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

public class NestWorkReferenceQueryTests
{
    [Fact]
    public async Task Item_reservations_describe_opennest_pep_and_drawing_holds_with_job_and_drawing_first()
    {
        await using var db = CreateDb();
        var item = await SeedReservationsAsync(db);

        var summaries = await new InventoryQueryService(db, freshnessThresholdDays: 90).GetItemReservationsAsync(item.InventoryItemId);

        Assert.Equal(
            ["Job JOB-1", "Drawing DWG-1 rev A", "Nest ON-NEST-7", "Nest 2026: NEST-31", "Nest NEST-NOYEAR", null],
            summaries.OrderBy(x => x.ReservationId).Select(x => x.WorkReference));
    }

    [Fact]
    public async Task Work_queue_describes_opennest_pep_and_drawing_holds_with_job_and_drawing_first()
    {
        await using var db = CreateDb();
        await SeedReservationsAsync(db);

        var summary = await new WorkQueueQueryService(db, new InventoryQueryService(db, freshnessThresholdDays: 90)).GetAsync();

        Assert.Equal(
            ["Job JOB-1", "Drawing DWG-1 rev A", "Nest ON-NEST-7", "Nest 2026: NEST-31", "Nest NEST-NOYEAR", "Unassigned material hold"],
            summary.RecentWork.OrderBy(x => x.MaterialReservationId).Select(x => x.WorkReference));
    }

    private static async Task<InventoryItem> SeedReservationsAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { AppUserId = 1, Username = "holder", DisplayName = "Holder", PasswordHash = "hash" };
        var spec = new MaterialSpec { MaterialSpecId = 1, MaterialFamily = "Steel", DisplayName = "Steel", ThicknessIn = .25m, IsActive = true };
        var location = new InventoryLocation { InventoryLocationId = 1, LocationCode = "S-1", DisplayName = "Shelf 1", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        var item = new InventoryItem
        {
            InventoryItemId = 1, PublicItemCode = "SHEET-1", MaterialSpec = spec, InventoryLocation = location,
            ItemKind = InventoryItemKind.FullSheetBundle, Status = InventoryItemStatus.Available, QuantityOnHand = 10,
            ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedByUser = user
        };
        var nestId = Guid.Parse("6f9a1c1e-3c55-4d7e-9b0e-2a4f1d2c3b4a");
        var createdAt = DateTime.UtcNow;
        db.AddRange(
            item,
            Hold(1, item, user, createdAt, r => { r.ShopOpsJobId = "JOB-1"; r.DrawingId = "DWG-1"; r.OpenNestNestId = nestId; r.NestName = "ON-NEST-7"; }),
            Hold(2, item, user, createdAt, r => { r.DrawingId = "DWG-1"; r.DrawingRevision = "A"; r.OpenNestNestId = nestId; r.NestName = "ON-NEST-7"; }),
            Hold(3, item, user, createdAt, r => { r.OpenNestNestId = nestId; r.NestName = "ON-NEST-7"; }),
            Hold(4, item, user, createdAt, r => { r.PepNestYear = 2026; r.PepNestName = "NEST-31"; }),
            Hold(5, item, user, createdAt, r => r.PepNestName = "NEST-NOYEAR"),
            Hold(6, item, user, createdAt, _ => { }));
        await db.SaveChangesAsync();
        return item;
    }

    private static MaterialReservation Hold(long id, InventoryItem item, AppUser user, DateTime createdAt, Action<MaterialReservation> configure)
    {
        var reservation = new MaterialReservation
        {
            MaterialReservationId = id, InventoryItem = item, Status = ReservationStatus.Proposed, Equipment = ReservationEquipment.Laser,
            ReservedQuantity = 1, CreatedAtUtc = createdAt, CreatedByUser = user
        };
        configure(reservation);
        return reservation;
    }

    private static MaterialLedgerDbContext CreateDb() => new(new DbContextOptionsBuilder<MaterialLedgerDbContext>()
        .UseInMemoryDatabase($"nest-work-reference-{Guid.NewGuid():N}").Options);
}
