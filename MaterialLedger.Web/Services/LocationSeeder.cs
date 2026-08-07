using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Services;

public class LocationSeeder(MaterialLedgerDbContext db)
{
    private record SeedLocation(
        string Code, string DisplayName, InventoryLocationType Type,
        InventoryStorageMode Mode, int SortOrder, string? ParentCode);

    private static readonly SeedLocation[] SeedLocations =
    [
        new("LASER-MATERIAL", "Laser Material Storage Area", InventoryLocationType.Area, InventoryStorageMode.Other, 0, null),
        new("L1-SOUTH-ROLL", "Laser 1 South Roll-Out Rack", InventoryLocationType.Rack, InventoryStorageMode.RolloutShelf, 1, "LASER-MATERIAL"),
        new("L1-NORTH-ROLL", "Laser 1 North Roll-Out Rack", InventoryLocationType.Rack, InventoryStorageMode.RolloutShelf, 2, "LASER-MATERIAL"),
        new("L2-ROLL", "Laser 2 Roll-Out Rack", InventoryLocationType.Rack, InventoryStorageMode.RolloutShelf, 3, "LASER-MATERIAL"),
        new("MID-ROLL", "Mid-Floor Roll-Out Rack", InventoryLocationType.Rack, InventoryStorageMode.RolloutShelf, 4, "LASER-MATERIAL"),
        new("EAST-DROP", "East Drop Rack", InventoryLocationType.Rack, InventoryStorageMode.VerticalDropBay, 5, "LASER-MATERIAL"),
        new("MID-DROP", "Mid Drop Rack", InventoryLocationType.Rack, InventoryStorageMode.VerticalDropBay, 6, "LASER-MATERIAL"),
        new("STAINLESS-DROP", "Stainless Drop Rack", InventoryLocationType.Rack, InventoryStorageMode.VerticalDropBay, 7, "LASER-MATERIAL"),
        new("SE-DROP", "Southeast Drop Rack", InventoryLocationType.Rack, InventoryStorageMode.VerticalDropBay, 8, "LASER-MATERIAL"),
        new("EAST-TAN", "East Tan Rack", InventoryLocationType.Rack, InventoryStorageMode.Other, 9, "LASER-MATERIAL"),
        new("WEST-TAN", "West Tan Rack", InventoryLocationType.Rack, InventoryStorageMode.Other, 10, "LASER-MATERIAL"),
        new("SKID-EAST-WEST", "Skid Storage (between East Tan and West Tan)", InventoryLocationType.Rack, InventoryStorageMode.FloorStaging, 11, "LASER-MATERIAL"),
        new("SKID-EAST-STAINLESS", "Skid Storage (between East Tan and Stainless Drop)", InventoryLocationType.Rack, InventoryStorageMode.FloorStaging, 12, "LASER-MATERIAL"),
    ];

    public async Task SeedAsync()
    {
        var codeToId = await db.InventoryLocations.ToDictionaryAsync(x => x.LocationCode, x => x.InventoryLocationId);

        foreach (var seed in SeedLocations.Where(s => s.ParentCode is null))
        {
            if (codeToId.ContainsKey(seed.Code))
                continue;
            var entity = ToEntity(seed, parentId: null);
            db.InventoryLocations.Add(entity);
            await db.SaveChangesAsync();
            codeToId[seed.Code] = entity.InventoryLocationId;
        }

        foreach (var seed in SeedLocations.Where(s => s.ParentCode is not null))
        {
            if (codeToId.ContainsKey(seed.Code))
                continue;
            var entity = ToEntity(seed, parentId: codeToId[seed.ParentCode!]);
            db.InventoryLocations.Add(entity);
            await db.SaveChangesAsync();
            codeToId[seed.Code] = entity.InventoryLocationId;
        }
    }

    private static InventoryLocation ToEntity(SeedLocation seed, long? parentId) => new()
    {
        LocationCode = seed.Code,
        DisplayName = seed.DisplayName,
        LocationType = seed.Type,
        StorageMode = seed.Mode,
        SortOrder = seed.SortOrder,
        ParentInventoryLocationId = parentId,
        CapacityConfirmed = false,
        IsActive = true
    };
}
