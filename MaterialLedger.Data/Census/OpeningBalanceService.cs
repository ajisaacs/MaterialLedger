using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MaterialLedger.Data.Census;

public record OpeningBalanceRequest(
    long InventoryLocationId,
    long MaterialSpecId,
    InventoryItemKind ItemKind,
    decimal Quantity,
    decimal? UsableWidthIn,
    decimal? UsableLengthIn,
    ShapeClassification ShapeClassification,
    bool IsQuarantined,
    string? Notes);

public class OpeningBalanceResult
{
    public required long InventoryItemId { get; init; }
    public required string PublicItemCode { get; init; }
}

public class OpeningBalanceService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    private static readonly InventoryLocationType[] LeafLocationTypes =
    [
        InventoryLocationType.Shelf, InventoryLocationType.Bay, InventoryLocationType.Staging,
        InventoryLocationType.Quarantine, InventoryLocationType.Scrap
    ];

    public async Task<IReadOnlyList<InventoryItem>> GetActiveItemsAtLocationAsync(long locationId) =>
        await db.InventoryItems
            .Where(x => x.InventoryLocationId == locationId
                && x.Status != InventoryItemStatus.Depleted
                && x.Status != InventoryItemStatus.Scrapped)
            .Include(x => x.MaterialSpec)
            .ToListAsync();

    public async Task<OpeningBalanceResult> RecordOpeningBalanceAsync(Guid correlationId, OpeningBalanceRequest request, int observedBy)
    {
        Validate(request);

        var payloadHash = HashPayload(request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "RecordOpeningBalance", payloadHash, observedBy);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<OpeningBalanceResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var location = await db.InventoryLocations.FindAsync(request.InventoryLocationId)
            ?? throw new InvalidOperationException($"Location {request.InventoryLocationId} does not exist.");
        if (!LeafLocationTypes.Contains(location.LocationType))
            throw new InvalidOperationException(
                $"Location '{location.LocationCode}' is a {location.LocationType} — inventory can only be recorded at a leaf location.");

        long nextSeq;
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT NEXT VALUE FOR material.ItemCodeSeq";
            command.Transaction = (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
            var scalarResult = await command.ExecuteScalarAsync();
            nextSeq = (long)scalarResult!;
        }
        var prefix = request.ItemKind == InventoryItemKind.Drop ? "DROP" : "SHEET";
        var publicItemCode = $"{prefix}-{nextSeq}";

        var observedAt = DateTime.UtcNow;
        var item = new InventoryItem
        {
            PublicItemCode = publicItemCode,
            MaterialSpecId = request.MaterialSpecId,
            InventoryLocationId = request.InventoryLocationId,
            ItemKind = request.ItemKind,
            Status = request.IsQuarantined ? InventoryItemStatus.Quarantined : InventoryItemStatus.Available,
            QuantityOnHand = request.Quantity,
            UsableWidthIn = request.UsableWidthIn,
            UsableLengthIn = request.UsableLengthIn,
            ShapeClassification = request.ShapeClassification,
            LastObservedAtUtc = observedAt,
            LastObservedBy = observedBy,
            Notes = request.Notes
        };
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        db.MaterialTransactions.Add(new MaterialTransaction
        {
            OccurredAtUtc = observedAt,
            RecordedAtUtc = observedAt,
            RecordedBy = observedBy,
            TransactionType = MaterialTransactionType.OpeningBalance,
            InventoryItemId = item.InventoryItemId,
            QuantityDelta = request.Quantity,
            GeometryWidthInSnapshot = request.UsableWidthIn,
            GeometryLengthInSnapshot = request.UsableLengthIn,
            Notes = request.Notes,
            SourceSystem = MaterialTransactionSourceSystem.OperatorUI
        });

        var result = new OpeningBalanceResult { InventoryItemId = item.InventoryItemId, PublicItemCode = publicItemCode };
        operationLog.Record(correlationId, "RecordOpeningBalance", payloadHash, JsonSerializer.Serialize(result), observedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static void Validate(OpeningBalanceRequest request)
    {
        if (request.ItemKind == InventoryItemKind.Drop)
        {
            if (request.Quantity != 1)
                throw new ArgumentException("An opening balance for a drop must record exactly 1 unit.", nameof(request));
            if (request.UsableWidthIn is null or <= 0 || request.UsableLengthIn is null or <= 0)
                throw new ArgumentException("A drop requires a usable width and length greater than zero.", nameof(request));
        }
        else
        {
            if (request.Quantity < 1 || request.Quantity != decimal.Truncate(request.Quantity))
                throw new ArgumentException("Full sheets/bundles must be recorded in whole-sheet quantities of at least 1.", nameof(request));
        }
    }

    private static string HashPayload(OpeningBalanceRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
}
