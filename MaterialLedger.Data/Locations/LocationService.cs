using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Locations;

public record CreateLocationRequest(
    string LocationCode,
    string DisplayName,
    InventoryLocationType LocationType,
    InventoryStorageMode StorageMode,
    long? ParentInventoryLocationId);

public class LocationService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    private const int LocationCodeMaxLength = 80;
    private const int DisplayNameMaxLength = 160;

    public async Task<InventoryLocation> CreateLocationAsync(
        Guid correlationId, CreateLocationRequest request, int executedBy)
    {
        var normalizedCode = NormalizeCode(request.LocationCode);
        ValidateCode(normalizedCode);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
            throw new InvalidOperationException("Display name is required.");
        if (request.DisplayName.Trim().Length > DisplayNameMaxLength)
            throw new InvalidOperationException($"Display name cannot exceed {DisplayNameMaxLength} characters.");

        if (request.LocationType is not (InventoryLocationType.Area or InventoryLocationType.Rack))
            throw new InvalidOperationException($"Location type '{request.LocationType}' cannot be created directly — only Area and Rack are supported here.");

        var payloadHash = HashPayload(normalizedCode, request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "CreateLocation", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replayId = JsonSerializer.Deserialize<long>(outcome.ResultSummary!);
            var replay = await db.InventoryLocations.FirstAsync(x => x.InventoryLocationId == replayId);
            await transaction.CommitAsync();
            return replay;
        }

        if (await db.InventoryLocations.AnyAsync(x => x.LocationCode == normalizedCode))
            throw new InvalidOperationException($"Location code '{normalizedCode}' already exists.");

        InventoryLocation? parent = null;
        if (request.LocationType == InventoryLocationType.Area)
        {
            if (request.ParentInventoryLocationId is not null)
                throw new InvalidOperationException("An Area cannot have a parent location.");
        }
        else
        {
            if (request.ParentInventoryLocationId is null)
                throw new InvalidOperationException("A Rack must have a parent Area.");

            parent = await db.InventoryLocations.FirstOrDefaultAsync(x => x.InventoryLocationId == request.ParentInventoryLocationId)
                ?? throw new InvalidOperationException("Parent location does not exist.");

            if (parent.LocationType != InventoryLocationType.Area)
                throw new InvalidOperationException($"Parent location '{parent.LocationCode}' is a {parent.LocationType}, not an Area — Racks must be created under an Area.");
        }

        var maxSiblingSortOrder = await db.InventoryLocations
            .Where(x => x.ParentInventoryLocationId == request.ParentInventoryLocationId)
            .Select(x => (int?)x.SortOrder)
            .MaxAsync() ?? -1;

        var entity = new InventoryLocation
        {
            LocationCode = normalizedCode,
            DisplayName = request.DisplayName.Trim(),
            LocationType = request.LocationType,
            StorageMode = request.LocationType == InventoryLocationType.Area ? InventoryStorageMode.Other : request.StorageMode,
            SortOrder = maxSiblingSortOrder + 1,
            ParentInventoryLocationId = request.ParentInventoryLocationId,
            CapacityConfirmed = false,
            IsActive = true
        };
        db.InventoryLocations.Add(entity);
        await db.SaveChangesAsync();

        operationLog.Record(correlationId, "CreateLocation", payloadHash, JsonSerializer.Serialize(entity.InventoryLocationId), executedBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return entity;
    }

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();

    private static void ValidateCode(string normalizedCode)
    {
        if (normalizedCode.Length == 0)
            throw new InvalidOperationException("Location code is required.");
        if (normalizedCode.Length > LocationCodeMaxLength)
            throw new InvalidOperationException($"Location code cannot exceed {LocationCodeMaxLength} characters.");
        if (!normalizedCode.All(c => IsValidCodeCharacter(c)))
            throw new InvalidOperationException("Location code may only contain letters, numbers, and hyphens.");
    }

    private static bool IsValidCodeCharacter(char c)
    {
        return (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';
    }

    private static string HashPayload(string normalizedCode, CreateLocationRequest request)
    {
        var payload = JsonSerializer.Serialize(new
        {
            normalizedCode, request.DisplayName, request.LocationType, request.StorageMode, request.ParentInventoryLocationId
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
