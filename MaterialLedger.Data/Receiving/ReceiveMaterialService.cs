using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MaterialLedger.Data.Receiving;

public record ReceiveMaterialRequest(
    long InventoryLocationId,
    long MaterialSpecId,
    InventoryItemKind ItemKind,
    decimal Quantity,
    decimal? UsableWidthIn,
    decimal? UsableLengthIn,
    ShapeClassification ShapeClassification,
    bool IsQuarantined,
    string? VendorName,
    string PurchaseOrderNumber,
    string? ReceiptNumber,
    string? VendorPartNumber,
    string? HeatLotNumber,
    decimal? CostPerUnit,
    string? Notes);

public class ReceiveMaterialResult
{
    public required long InventoryItemId { get; init; }
    public required long SheetLotId { get; init; }
    public required string PublicItemCode { get; init; }
}

public class ReceiveMaterialService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    private static readonly InventoryLocationType[] LeafLocationTypes =
    [
        InventoryLocationType.Shelf, InventoryLocationType.Bay, InventoryLocationType.Staging,
        InventoryLocationType.Quarantine, InventoryLocationType.Scrap
    ];

    public async Task<ReceiveMaterialResult> ReceiveMaterialAsync(Guid correlationId, ReceiveMaterialRequest request, int receivedBy)
    {
        Validate(request);

        var payloadHash = HashPayload(request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "ReceiveMaterial", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<ReceiveMaterialResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var location = await db.InventoryLocations.FindAsync(request.InventoryLocationId)
            ?? throw new InvalidOperationException($"Location {request.InventoryLocationId} does not exist.");
        if (!LeafLocationTypes.Contains(location.LocationType))
            throw new InvalidOperationException(
                $"Location '{location.LocationCode}' is a {location.LocationType} — inventory can only be recorded at a leaf location.");

        var receivedAt = DateTime.UtcNow;
        var sheetLot = new SheetLot
        {
            MaterialSpecId = request.MaterialSpecId,
            VendorName = request.VendorName,
            PurchaseOrderNumber = request.PurchaseOrderNumber,
            ReceiptNumber = request.ReceiptNumber,
            VendorPartNumber = request.VendorPartNumber,
            HeatLotNumber = request.HeatLotNumber,
            CostPerUnit = request.CostPerUnit,
            ReceivedAtUtc = receivedAt,
            SourceType = SheetLotSourceType.Receipt
        };
        db.SheetLots.Add(sheetLot);
        await db.SaveChangesAsync();

        long nextSeq;
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT NEXT VALUE FOR material.ItemCodeSeq";
            command.Transaction = (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
            var scalarResult = await command.ExecuteScalarAsync();
            nextSeq = (long)scalarResult!;
        }
        var publicItemCode = $"SHEET-{nextSeq}";

        var item = new InventoryItem
        {
            PublicItemCode = publicItemCode,
            MaterialSpecId = request.MaterialSpecId,
            SheetLotId = sheetLot.SheetLotId,
            InventoryLocationId = request.InventoryLocationId,
            ItemKind = request.ItemKind,
            Status = request.IsQuarantined ? InventoryItemStatus.Quarantined : InventoryItemStatus.Available,
            QuantityOnHand = request.Quantity,
            UsableWidthIn = request.UsableWidthIn,
            UsableLengthIn = request.UsableLengthIn,
            ShapeClassification = request.ShapeClassification,
            LastObservedAtUtc = receivedAt,
            LastObservedBy = receivedBy,
            Notes = request.Notes
        };
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        db.MaterialTransactions.Add(new MaterialTransaction
        {
            OccurredAtUtc = receivedAt,
            RecordedAtUtc = receivedAt,
            RecordedBy = receivedBy,
            TransactionType = MaterialTransactionType.Receipt,
            InventoryItemId = item.InventoryItemId,
            SheetLotId = sheetLot.SheetLotId,
            QuantityDelta = request.Quantity,
            GeometryWidthInSnapshot = request.UsableWidthIn,
            GeometryLengthInSnapshot = request.UsableLengthIn,
            Notes = request.Notes,
            SourceSystem = MaterialTransactionSourceSystem.OperatorUI
        });

        var result = new ReceiveMaterialResult
        {
            InventoryItemId = item.InventoryItemId,
            SheetLotId = sheetLot.SheetLotId,
            PublicItemCode = publicItemCode
        };
        operationLog.Record(correlationId, "ReceiveMaterial", payloadHash, JsonSerializer.Serialize(result), receivedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static void Validate(ReceiveMaterialRequest request)
    {
        if (request.ItemKind == InventoryItemKind.Drop)
            throw new ArgumentException("ReceiveMaterial only accepts full sheets or bundles, not drops.", nameof(request));
        if (request.Quantity < 1 || request.Quantity != decimal.Truncate(request.Quantity))
            throw new ArgumentException("Received quantity must be a whole number of at least 1 sheet.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PurchaseOrderNumber))
            throw new ArgumentException("A purchase order number is required to receive material.", nameof(request));
    }

    private static string HashPayload(ReceiveMaterialRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
}
