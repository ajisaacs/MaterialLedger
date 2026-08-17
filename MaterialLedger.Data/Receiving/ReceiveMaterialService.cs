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
    long? VendorId,
    string PurchaseOrderNumber,
    string? ReceiptNumber,
    long? VendorPartId,
    string? HeatLotNumber,
    decimal? CostPerUnit,
    string? Notes,
    long? PurchaseOrderLineId = null);

public class ReceiveMaterialResult
{
    public required long InventoryItemId { get; init; }
    public required long SheetLotId { get; init; }
    public required string PublicItemCode { get; init; }
}

public interface IReceiveMaterialService
{
    Task<IdempotentResult<ReceiveMaterialResult>> ReceiveMaterialAsync(Guid correlationId, ReceiveMaterialRequest request, int receivedBy);
}

public class ReceiveMaterialService(MaterialLedgerDbContext db, OperationLogGuard operationLog) : IReceiveMaterialService
{
    private static readonly InventoryLocationType[] LeafLocationTypes =
    [
        InventoryLocationType.Shelf, InventoryLocationType.Bay, InventoryLocationType.Staging,
        InventoryLocationType.Quarantine, InventoryLocationType.Scrap
    ];

    public async Task<IdempotentResult<ReceiveMaterialResult>> ReceiveMaterialAsync(Guid correlationId, ReceiveMaterialRequest request, int receivedBy)
    {
        Validate(request);

        var payloadHash = HashPayload(request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "ReceiveMaterial", payloadHash, receivedBy);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<ReceiveMaterialResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return new IdempotentResult<ReceiveMaterialResult>(replay, WasReplayed: true);
        }

        var location = await db.InventoryLocations.FindAsync(request.InventoryLocationId)
            ?? throw new InvalidOperationException($"Location {request.InventoryLocationId} does not exist.");
        if (!LeafLocationTypes.Contains(location.LocationType))
            throw new InvalidOperationException(
                $"Location '{location.LocationCode}' is a {location.LocationType} — inventory can only be recorded at a leaf location.");

        PurchaseOrderLine? purchaseOrderLine = null;
        Vendor? vendor = null;
        VendorPart? vendorPart = null;
        if (request.PurchaseOrderLineId is not null)
        {
            purchaseOrderLine = await db.PurchaseOrderLines
                .Include(x => x.PurchaseOrder)
                .Include(x => x.VendorPart)
                .FirstOrDefaultAsync(x => x.PurchaseOrderLineId == request.PurchaseOrderLineId.Value)
                ?? throw new InvalidOperationException($"Purchase order line {request.PurchaseOrderLineId} does not exist.");
            if (purchaseOrderLine.MaterialSpecId != request.MaterialSpecId)
                throw new InvalidOperationException(
                    $"Purchase order line {purchaseOrderLine.PurchaseOrderLineId} was ordered for a different material — receive it without a PO line, or correct the material.");
            if (purchaseOrderLine.Status is PurchaseOrderLineStatus.Cancelled or PurchaseOrderLineStatus.Received)
                throw new InvalidOperationException(
                    $"Purchase order line {purchaseOrderLine.PurchaseOrderLineId} is {purchaseOrderLine.Status} and cannot receive more material.");
            var remaining = purchaseOrderLine.QuantityOrdered - purchaseOrderLine.QuantityReceived;
            if (request.Quantity > remaining)
                throw new InvalidOperationException(
                    $"Cannot receive {request.Quantity} — only {remaining} remains outstanding on purchase order line {purchaseOrderLine.PurchaseOrderLineId}.");

            var order = purchaseOrderLine.PurchaseOrder!;
            if (request.VendorId is not null && request.VendorId != order.VendorId)
                throw new InvalidOperationException("The supplied vendor does not match the purchase order.");
            if (request.VendorPartId is not null && request.VendorPartId != purchaseOrderLine.VendorPartId)
                throw new InvalidOperationException("The supplied vendor part does not match the purchase order line.");

            vendor = await db.Vendors.FindAsync(order.VendorId)
                ?? throw new InvalidOperationException($"Purchase order vendor {order.VendorId} does not exist.");
            vendorPart = purchaseOrderLine.VendorPart;
        }
        else if (request.VendorId is not null)
        {
            vendor = await db.Vendors.FindAsync(request.VendorId.Value)
                ?? throw new InvalidOperationException($"Vendor {request.VendorId} does not exist.");
            if (!vendor.IsActive)
                throw new InvalidOperationException($"Vendor {vendor.VendorId} is inactive and cannot be selected for a new receipt.");
            if (request.VendorPartId is not null)
            {
                vendorPart = await db.VendorParts.FindAsync(request.VendorPartId.Value)
                    ?? throw new InvalidOperationException($"Vendor part {request.VendorPartId} does not exist.");
                if (vendorPart.VendorId != vendor.VendorId)
                    throw new InvalidOperationException($"Vendor part {vendorPart.VendorPartId} does not belong to vendor {vendor.VendorId}.");
                if (!vendorPart.IsActive)
                    throw new InvalidOperationException($"Vendor part {vendorPart.VendorPartId} is inactive and cannot be selected for a new receipt.");
            }
        }
        else if (request.VendorPartId is not null)
        {
            throw new InvalidOperationException("A vendor part requires a vendor.");
        }

        var receivedAt = DateTime.UtcNow;
        var sheetLot = new SheetLot
        {
            MaterialSpecId = request.MaterialSpecId,
            VendorId = vendor?.VendorId,
            VendorName = vendor?.Name,
            PurchaseOrderNumber = purchaseOrderLine?.PurchaseOrder?.PurchaseOrderNumber ?? request.PurchaseOrderNumber,
            ReceiptNumber = request.ReceiptNumber,
            VendorPartId = vendorPart?.VendorPartId,
            VendorPartNumber = vendorPart?.PartNumber,
            HeatLotNumber = request.HeatLotNumber,
            CostPerUnit = request.CostPerUnit,
            ReceivedAtUtc = receivedAt,
            SourceType = SheetLotSourceType.Receipt,
            PurchaseOrderLineId = purchaseOrderLine?.PurchaseOrderLineId
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

        if (purchaseOrderLine is not null)
        {
            purchaseOrderLine.QuantityReceived += request.Quantity;
            purchaseOrderLine.Status = purchaseOrderLine.QuantityReceived >= purchaseOrderLine.QuantityOrdered
                ? PurchaseOrderLineStatus.Received
                : PurchaseOrderLineStatus.PartiallyReceived;
        }

        var result = new ReceiveMaterialResult
        {
            InventoryItemId = item.InventoryItemId,
            SheetLotId = sheetLot.SheetLotId,
            PublicItemCode = publicItemCode
        };
        operationLog.Record(correlationId, "ReceiveMaterial", payloadHash, JsonSerializer.Serialize(result), receivedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new IdempotentResult<ReceiveMaterialResult>(result, WasReplayed: false);
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
