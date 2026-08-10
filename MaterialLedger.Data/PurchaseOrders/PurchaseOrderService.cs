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

    private static void Validate(CreatePurchaseOrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.VendorName))
            throw new ArgumentException("Vendor name is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PurchaseOrderNumber))
            throw new ArgumentException("Purchase order number is required.", nameof(request));
        if (request.Lines.Count == 0)
            throw new ArgumentException("A purchase order must have at least one line.", nameof(request));
        foreach (var line in request.Lines)
        {
            if (line.QuantityOrdered < 1 || line.QuantityOrdered != decimal.Truncate(line.QuantityOrdered))
                throw new ArgumentException("Each line's ordered quantity must be a whole number of at least 1.", nameof(request));
        }
    }

    private static string HashPayload<T>(T payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
}
