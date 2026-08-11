using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.PurchaseOrders;

public record PurchaseOrderLineRequest(
    long MaterialSpecId,
    string? VendorDescription,
    string? VendorPartNumber,
    decimal QuantityOrdered,
    decimal? UsableWidthIn,
    decimal? UsableLengthIn,
    decimal? UnitPrice,
    DateOnly? QuotedDeliveryDate);

public record CreatePurchaseOrderRequest(
    string VendorName,
    string PurchaseOrderNumber,
    DateOnly? OrderDate,
    string? Notes,
    IReadOnlyList<PurchaseOrderLineRequest> Lines);

public record AddLocationAllocationRequest(long PurchaseOrderLineId, long InventoryLocationId, decimal Quantity);

public class PurchaseOrderService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    public async Task<long> CreatePurchaseOrderAsync(Guid correlationId, CreatePurchaseOrderRequest request, int createdBy)
    {
        Validate(request);

        var payloadHash = HashPayload(request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "CreatePurchaseOrder", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replayId = JsonSerializer.Deserialize<long>(outcome.ResultSummary!);
            await transaction.CommitAsync();
            return replayId;
        }

        var order = new PurchaseOrder
        {
            VendorName = request.VendorName.Trim(),
            PurchaseOrderNumber = request.PurchaseOrderNumber.Trim(),
            OrderDate = request.OrderDate,
            Notes = request.Notes,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = createdBy
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        var lineNumber = 1;
        foreach (var line in request.Lines)
        {
            db.PurchaseOrderLines.Add(new PurchaseOrderLine
            {
                PurchaseOrderId = order.PurchaseOrderId,
                LineNumber = lineNumber++,
                MaterialSpecId = line.MaterialSpecId,
                VendorDescription = line.VendorDescription,
                VendorPartNumber = line.VendorPartNumber,
                QuantityOrdered = line.QuantityOrdered,
                QuantityReceived = 0,
                UsableWidthIn = line.UsableWidthIn,
                UsableLengthIn = line.UsableLengthIn,
                UnitPrice = line.UnitPrice,
                QuotedDeliveryDate = line.QuotedDeliveryDate,
                Status = PurchaseOrderLineStatus.Open
            });
        }
        await db.SaveChangesAsync();

        operationLog.Record(correlationId, "CreatePurchaseOrder", payloadHash, JsonSerializer.Serialize(order.PurchaseOrderId), createdBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return order.PurchaseOrderId;
    }

    public async Task<int> CancelLineAsync(Guid correlationId, long purchaseOrderLineId, int cancelledBy)
    {
        var payloadHash = HashPayload(new { purchaseOrderLineId });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "CancelPurchaseOrderLine", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replayCount = JsonSerializer.Deserialize<int>(outcome.ResultSummary!);
            await transaction.CommitAsync();
            return replayCount;
        }

        var line = await db.PurchaseOrderLines.FindAsync(purchaseOrderLineId)
            ?? throw new InvalidOperationException($"Purchase order line {purchaseOrderLineId} does not exist.");
        CancelLineCore(line);
        await db.SaveChangesAsync();

        operationLog.Record(correlationId, "CancelPurchaseOrderLine", payloadHash, JsonSerializer.Serialize(1), cancelledBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return 1;
    }

    public async Task<int> CloseRemainingAsync(Guid correlationId, long purchaseOrderId, int closedBy)
    {
        var payloadHash = HashPayload(new { purchaseOrderId });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "ClosePurchaseOrderRemaining", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replayCount = JsonSerializer.Deserialize<int>(outcome.ResultSummary!);
            await transaction.CommitAsync();
            return replayCount;
        }

        var openLines = await db.PurchaseOrderLines
            .Where(x => x.PurchaseOrderId == purchaseOrderId
                && (x.Status == PurchaseOrderLineStatus.Open || x.Status == PurchaseOrderLineStatus.PartiallyReceived))
            .ToListAsync();
        foreach (var line in openLines)
            CancelLineCore(line);
        await db.SaveChangesAsync();

        operationLog.Record(correlationId, "ClosePurchaseOrderRemaining", payloadHash, JsonSerializer.Serialize(openLines.Count), closedBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return openLines.Count;
    }

    private static readonly InventoryLocationType[] LeafLocationTypes =
    [
        InventoryLocationType.Shelf, InventoryLocationType.Bay, InventoryLocationType.Staging,
        InventoryLocationType.Quarantine, InventoryLocationType.Scrap
    ];

    public async Task<long> AddLocationAllocationAsync(Guid correlationId, AddLocationAllocationRequest request, int createdBy)
    {
        ValidateAllocation(request);

        var payloadHash = HashPayload(request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "AddPurchaseOrderLineAllocation", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replayId = JsonSerializer.Deserialize<long>(outcome.ResultSummary!);
            await transaction.CommitAsync();
            return replayId;
        }

        var line = await db.PurchaseOrderLines
            .Include(x => x.LocationAllocations)
            .FirstOrDefaultAsync(x => x.PurchaseOrderLineId == request.PurchaseOrderLineId)
            ?? throw new InvalidOperationException($"Purchase order line {request.PurchaseOrderLineId} does not exist.");
        if (line.Status is PurchaseOrderLineStatus.Received or PurchaseOrderLineStatus.Cancelled)
            throw new InvalidOperationException(
                $"Purchase order line {line.PurchaseOrderLineId} is {line.Status} and cannot have a delivery location planned for it.");

        var location = await db.InventoryLocations.FindAsync(request.InventoryLocationId)
            ?? throw new InvalidOperationException($"Location {request.InventoryLocationId} does not exist.");
        if (!LeafLocationTypes.Contains(location.LocationType))
            throw new InvalidOperationException(
                $"Location '{location.LocationCode}' is a {location.LocationType} — allocations can only target a leaf location.");

        var alreadyAllocated = line.LocationAllocations.Sum(x => x.QuantityPlanned);
        var remaining = line.QuantityOrdered - line.QuantityReceived;
        if (alreadyAllocated + request.Quantity > remaining)
            throw new InvalidOperationException(
                $"Cannot allocate {request.Quantity} — only {remaining - alreadyAllocated} of purchase order line {line.PurchaseOrderLineId} remains unallocated.");

        var allocation = new PurchaseOrderLineAllocation
        {
            PurchaseOrderLineId = line.PurchaseOrderLineId,
            InventoryLocationId = request.InventoryLocationId,
            QuantityPlanned = request.Quantity,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = createdBy
        };
        db.PurchaseOrderLineAllocations.Add(allocation);
        await db.SaveChangesAsync();

        operationLog.Record(correlationId, "AddPurchaseOrderLineAllocation", payloadHash, JsonSerializer.Serialize(allocation.PurchaseOrderLineAllocationId), createdBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return allocation.PurchaseOrderLineAllocationId;
    }

    public async Task RemoveLocationAllocationAsync(Guid correlationId, long allocationId, int removedBy)
    {
        var payloadHash = HashPayload(new { allocationId });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "RemovePurchaseOrderLineAllocation", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            await transaction.CommitAsync();
            return;
        }

        var allocation = await db.PurchaseOrderLineAllocations.FindAsync(allocationId);
        if (allocation is not null)
            db.PurchaseOrderLineAllocations.Remove(allocation);
        await db.SaveChangesAsync();

        operationLog.Record(correlationId, "RemovePurchaseOrderLineAllocation", payloadHash, JsonSerializer.Serialize(true), removedBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static void ValidateAllocation(AddLocationAllocationRequest request)
    {
        if (request.Quantity < 1 || request.Quantity != decimal.Truncate(request.Quantity))
            throw new ArgumentException("Allocation quantity must be a whole number of at least 1.", nameof(request));
    }

    private static void CancelLineCore(PurchaseOrderLine line)
    {
        if (line.Status == PurchaseOrderLineStatus.Received)
            throw new InvalidOperationException($"Purchase order line {line.PurchaseOrderLineId} is already fully received and cannot be cancelled.");
        line.Status = PurchaseOrderLineStatus.Cancelled;
    }

    private static void Validate(CreatePurchaseOrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.VendorName))
            throw new ArgumentException("Vendor name is required.", nameof(request));
        if (request.VendorName.Trim().Length > 200)
            throw new ArgumentException("Vendor name cannot exceed 200 characters.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PurchaseOrderNumber))
            throw new ArgumentException("Purchase order number is required.", nameof(request));
        if (request.PurchaseOrderNumber.Trim().Length > 100)
            throw new ArgumentException("Purchase order number cannot exceed 100 characters.", nameof(request));
        if (request.Notes?.Length > 1000)
            throw new ArgumentException("Notes cannot exceed 1000 characters.", nameof(request));
        if (request.Lines.Count == 0)
            throw new ArgumentException("A purchase order must have at least one line.", nameof(request));
        foreach (var line in request.Lines)
        {
            if (line.QuantityOrdered < 1 || line.QuantityOrdered != decimal.Truncate(line.QuantityOrdered))
                throw new ArgumentException("Each line's ordered quantity must be a whole number of at least 1.", nameof(request));
            if (line.VendorDescription?.Length > 200)
                throw new ArgumentException("Vendor description cannot exceed 200 characters.", nameof(request));
            if (line.VendorPartNumber?.Length > 100)
                throw new ArgumentException("Vendor part number cannot exceed 100 characters.", nameof(request));
        }
    }

    private static string HashPayload<T>(T payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
}
