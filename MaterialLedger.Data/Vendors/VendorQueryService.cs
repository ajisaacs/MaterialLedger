using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Vendors;

public record PartActualPricePoint(DateTime ReceivedAtUtc, decimal CostPerUnit,
    string? PurchaseOrderNumber, string? ReceiptNumber, long SheetLotId);

public record PartQuotedPricePoint(DateOnly? OrderDate, decimal UnitPrice,
    decimal QuantityOrdered, PurchaseOrderLineStatus Status, long PurchaseOrderId);

public record PartPriceSummary(decimal? LastActual, DateTime? LastActualAt,
    decimal? AvgActual, int ActualPoints, int QuotedPoints);

public class VendorQueryService(MaterialLedgerDbContext db)
{
    public async Task<IReadOnlyList<Vendor>> GetVendorsAsync()
    {
        return await db.Vendors
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ThenBy(x => x.VendorId)
            .ToListAsync();
    }

    public async Task<VendorPart> GetVendorPartAsync(long vendorPartId)
    {
        return await db.VendorParts
            .AsNoTracking()
            .Include(x => x.Vendor)
            .SingleOrDefaultAsync(x => x.VendorPartId == vendorPartId)
            ?? throw new KeyNotFoundException($"Vendor part {vendorPartId} does not exist.");
    }

    public async Task<IReadOnlyList<PartActualPricePoint>> GetActualPriceHistoryAsync(long vendorPartId)
    {
        return await db.SheetLots
            .AsNoTracking()
            .Where(x => x.VendorPartId == vendorPartId
                && x.SourceType == SheetLotSourceType.Receipt
                && x.ReceivedAtUtc != null
                && x.CostPerUnit != null)
            .OrderByDescending(x => x.ReceivedAtUtc)
            .ThenByDescending(x => x.SheetLotId)
            .Select(x => new PartActualPricePoint(
                x.ReceivedAtUtc!.Value,
                x.CostPerUnit!.Value,
                x.PurchaseOrderNumber,
                x.ReceiptNumber,
                x.SheetLotId))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<PartQuotedPricePoint>> GetQuotedPriceHistoryAsync(long vendorPartId)
    {
        return await db.PurchaseOrderLines
            .AsNoTracking()
            .Where(x => x.VendorPartId == vendorPartId && x.UnitPrice != null)
            .OrderByDescending(x => x.PurchaseOrder!.OrderDate)
            .ThenByDescending(x => x.PurchaseOrderId)
            .Select(x => new PartQuotedPricePoint(
                x.PurchaseOrder!.OrderDate,
                x.UnitPrice!.Value,
                x.QuantityOrdered,
                x.Status,
                x.PurchaseOrderId))
            .ToListAsync();
    }

    public async Task<PartPriceSummary> GetPriceSummaryAsync(long vendorPartId)
    {
        var actuals = await ActualPricePoints(vendorPartId).ToListAsync();
        var quotedPoints = await db.PurchaseOrderLines
            .AsNoTracking()
            .CountAsync(x => x.VendorPartId == vendorPartId && x.UnitPrice != null);

        var latest = actuals
            .OrderByDescending(x => x.ReceivedAtUtc)
            .ThenByDescending(x => x.SheetLotId)
            .FirstOrDefault();

        return new PartPriceSummary(
            latest?.CostPerUnit,
            latest?.ReceivedAtUtc,
            actuals.Count == 0 ? null : actuals.Average(x => x.CostPerUnit),
            actuals.Count,
            quotedPoints);
    }

    private IQueryable<PartActualPricePoint> ActualPricePoints(long vendorPartId) =>
        db.SheetLots
            .AsNoTracking()
            .Where(x => x.VendorPartId == vendorPartId
                && x.SourceType == SheetLotSourceType.Receipt
                && x.ReceivedAtUtc != null
                && x.CostPerUnit != null)
            .Select(x => new PartActualPricePoint(
                x.ReceivedAtUtc!.Value,
                x.CostPerUnit!.Value,
                x.PurchaseOrderNumber,
                x.ReceiptNumber,
                x.SheetLotId));
}
