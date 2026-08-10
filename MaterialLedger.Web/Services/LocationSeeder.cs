using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Services;

public class LocationSeeder(MaterialLedgerDbContext db)
{
    private record SeedLocation(
        string Code, string DisplayName, InventoryLocationType Type,
        InventoryStorageMode Mode, int SortOrder, string? ParentCode, string? LegacyDisplayName = null);

    private static readonly SeedLocation[] SeedLocations =
    [
        new("LASER-MATERIAL", "Material Storage Area", InventoryLocationType.Area, InventoryStorageMode.Other, 0, null, "Laser Material Storage Area"),
        new("L1-SOUTH-ROLL", "South Roll-Out Rack", InventoryLocationType.Rack, InventoryStorageMode.RolloutShelf, 1, "LASER-MATERIAL", "Laser 1 South Roll-Out Rack"),
        new("L1-NORTH-ROLL", "North Roll-Out Rack", InventoryLocationType.Rack, InventoryStorageMode.RolloutShelf, 2, "LASER-MATERIAL", "Laser 1 North Roll-Out Rack"),
        new("L2-ROLL", "Roll-Out Rack 2", InventoryLocationType.Rack, InventoryStorageMode.RolloutShelf, 3, "LASER-MATERIAL", "Laser 2 Roll-Out Rack"),
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
        var locationsByCode = await db.InventoryLocations.ToDictionaryAsync(x => x.LocationCode);

        foreach (var seed in SeedLocations.Where(s => s.ParentCode is null))
        {
            if (locationsByCode.TryGetValue(seed.Code, out var existing))
            {
                UpdateLegacyDisplayName(existing, seed);
                continue;
            }

            var entity = ToEntity(seed, parentId: null);
            db.InventoryLocations.Add(entity);
            await db.SaveChangesAsync();
            locationsByCode[seed.Code] = entity;
        }

        foreach (var seed in SeedLocations.Where(s => s.ParentCode is not null))
        {
            if (locationsByCode.TryGetValue(seed.Code, out var existing))
            {
                UpdateLegacyDisplayName(existing, seed);
                continue;
            }

            var entity = ToEntity(seed, parentId: locationsByCode[seed.ParentCode!].InventoryLocationId);
            db.InventoryLocations.Add(entity);
            await db.SaveChangesAsync();
            locationsByCode[seed.Code] = entity;
        }

        await db.SaveChangesAsync();
    }

    private static void UpdateLegacyDisplayName(InventoryLocation existing, SeedLocation seed)
    {
        if (existing.DisplayName == seed.LegacyDisplayName)
            existing.DisplayName = seed.DisplayName;
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
