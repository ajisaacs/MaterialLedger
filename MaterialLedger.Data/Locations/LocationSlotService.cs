using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Locations;

public record SlotDefinition(string SlotCode, string DisplayName, int SortOrder);

public class LocationSlotResult
{
    public required long ParentInventoryLocationId { get; init; }
    public required IReadOnlyList<string> CreatedLocationCodes { get; init; }
}

public class LocationSlotConflictException(string message) : Exception(message);

public class LocationSlotService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    public async Task<LocationSlotResult> ConfirmLocationSlotsAsync(
        Guid correlationId, string parentLocationCode, IReadOnlyList<SlotDefinition> slots, int executedBy)
    {
        if (slots.Count == 0)
            throw new ArgumentException("At least one slot definition is required.", nameof(slots));

        var payloadHash = HashPayload(parentLocationCode, slots);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "ConfirmLocationSlots", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<LocationSlotResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var parent = await db.InventoryLocations.FirstOrDefaultAsync(x => x.LocationCode == parentLocationCode)
            ?? throw new InvalidOperationException($"Location '{parentLocationCode}' does not exist.");

        if (parent.LocationType != InventoryLocationType.Rack)
            throw new InvalidOperationException(
                $"Location '{parentLocationCode}' is a {parent.LocationType}, not a Rack — only racks can have slots confirmed.");

        var childType = ChildTypeFor(parent.StorageMode);
        var newCodes = slots.Select(s => $"{parentLocationCode}:{s.SlotCode}").ToList();

        var existingCodes = await db.InventoryLocations
            .Where(x => newCodes.Contains(x.LocationCode))
            .Select(x => x.LocationCode)
            .ToListAsync();
        if (existingCodes.Count > 0)
            throw new LocationSlotConflictException($"Slot code(s) already exist: {string.Join(", ", existingCodes)}");

        foreach (var slot in slots)
        {
            db.InventoryLocations.Add(new InventoryLocation
            {
                LocationCode = $"{parentLocationCode}:{slot.SlotCode}",
                DisplayName = slot.DisplayName,
                LocationType = childType,
                StorageMode = parent.StorageMode,
                SortOrder = slot.SortOrder,
                ParentInventoryLocationId = parent.InventoryLocationId,
                CapacityConfirmed = true,
                IsActive = true
            });
        }
        parent.CapacityConfirmed = true;

        var result = new LocationSlotResult { ParentInventoryLocationId = parent.InventoryLocationId, CreatedLocationCodes = newCodes };
        operationLog.Record(correlationId, "ConfirmLocationSlots", payloadHash, JsonSerializer.Serialize(result), executedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static InventoryLocationType ChildTypeFor(InventoryStorageMode parentMode) => parentMode switch
    {
        InventoryStorageMode.RolloutShelf => InventoryLocationType.Shelf,
        InventoryStorageMode.VerticalDropBay => InventoryLocationType.Bay,
        InventoryStorageMode.FloorStaging => InventoryLocationType.Staging,
        InventoryStorageMode.Other => InventoryLocationType.Shelf,
        _ => throw new ArgumentOutOfRangeException(nameof(parentMode))
    };

    private static string HashPayload(string parentLocationCode, IReadOnlyList<SlotDefinition> slots)
    {
        var payload = JsonSerializer.Serialize(new { parentLocationCode, slots });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
