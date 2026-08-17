using System.Security.Cryptography;
using System.Data;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Vendors;

public record CreateVendorRequest(string Name, string? ContactName, string? ContactEmail, string? ContactPhone, string? Notes);
public record UpdateVendorRequest(string Name, string? ContactName, string? ContactEmail, string? ContactPhone, string? Notes, bool IsActive);
public record CreateVendorPartRequest(long VendorId, string PartNumber, string? Description, decimal? MinimumOrderQty, string? Unit);

public class VendorService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    private const string CreateVendorProcedure = "VendorService.CreateVendor";
    private const string CreateVendorPartProcedure = "VendorService.CreateVendorPart";

    public async Task<Vendor> CreateVendorAsync(Guid correlationId, CreateVendorRequest request, int executedBy)
    {
        var name = RequireTrimmed(request.Name, 200, "Vendor name");
        ValidateOptional(request.ContactName, 200, "Contact name");
        ValidateOptional(request.ContactEmail, 320, "Contact email");
        ValidateOptional(request.ContactPhone, 50, "Contact phone");
        ValidateOptional(request.Notes, 1000, "Notes");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var hash = Hash(request);
        var outcome = await operationLog.CheckAsync(correlationId, CreateVendorProcedure, hash, executedBy);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
            return await FindRecordedVendorAsync(outcome.ResultSummary);

        var normalizedName = Normalize(name);
        if (await db.Vendors.AnyAsync(x => x.NormalizedName == normalizedName))
            throw new InvalidOperationException($"A vendor named '{name}' already exists.");

        var vendor = new Vendor
        {
            Name = name,
            ContactName = TrimOptional(request.ContactName),
            ContactEmail = TrimOptional(request.ContactEmail),
            ContactPhone = TrimOptional(request.ContactPhone),
            Notes = TrimOptional(request.Notes),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();
        operationLog.Record(correlationId, CreateVendorProcedure, hash, vendor.VendorId.ToString(), executedBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return vendor;
    }

    public async Task<Vendor> UpdateVendorAsync(long vendorId, UpdateVendorRequest request, int executedBy)
    {
        var name = RequireTrimmed(request.Name, 200, "Vendor name");
        ValidateOptional(request.ContactName, 200, "Contact name");
        ValidateOptional(request.ContactEmail, 320, "Contact email");
        ValidateOptional(request.ContactPhone, 50, "Contact phone");
        ValidateOptional(request.Notes, 1000, "Notes");

        var vendor = await db.Vendors.FindAsync(vendorId)
            ?? throw new InvalidOperationException($"Vendor {vendorId} does not exist.");
        var normalizedName = Normalize(name);
        if (await db.Vendors.AnyAsync(x => x.VendorId != vendorId && x.NormalizedName == normalizedName))
            throw new InvalidOperationException($"A vendor named '{name}' already exists.");

        vendor.Name = name;
        vendor.ContactName = TrimOptional(request.ContactName);
        vendor.ContactEmail = TrimOptional(request.ContactEmail);
        vendor.ContactPhone = TrimOptional(request.ContactPhone);
        vendor.Notes = TrimOptional(request.Notes);
        vendor.IsActive = request.IsActive;
        await db.SaveChangesAsync();
        return vendor;
    }

    public async Task<VendorPart> CreateVendorPartAsync(Guid correlationId, CreateVendorPartRequest request, int executedBy)
    {
        var partNumber = RequireTrimmed(request.PartNumber, 100, "Vendor part number");
        ValidateOptional(request.Description, 1000, "Description");
        ValidateOptional(request.Unit, 50, "Unit");
        if (request.MinimumOrderQty is <= 0)
            throw new ArgumentException("Minimum order quantity must be greater than zero when provided.", nameof(request));

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var hash = Hash(request);
        var outcome = await operationLog.CheckAsync(correlationId, CreateVendorPartProcedure, hash, executedBy);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
            return await FindRecordedVendorPartAsync(outcome.ResultSummary);

        var vendor = await db.Vendors.FindAsync(request.VendorId)
            ?? throw new InvalidOperationException($"Vendor {request.VendorId} does not exist.");
        var normalizedPartNumber = Normalize(partNumber);
        if (await db.VendorParts.AnyAsync(x => x.VendorId == vendor.VendorId && x.NormalizedPartNumber == normalizedPartNumber))
            throw new InvalidOperationException($"Vendor part '{partNumber}' already exists for vendor {vendor.VendorId}.");

        var part = new VendorPart
        {
            VendorId = vendor.VendorId,
            PartNumber = partNumber,
            Description = TrimOptional(request.Description),
            MinimumOrderQty = request.MinimumOrderQty,
            Unit = TrimOptional(request.Unit),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.VendorParts.Add(part);
        await db.SaveChangesAsync();
        operationLog.Record(correlationId, CreateVendorPartProcedure, hash, part.VendorPartId.ToString(), executedBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return part;
    }

    public async Task<Vendor> FindOrCreateActiveVendorAsync(string name, int executedBy)
    {
        var trimmed = RequireTrimmed(name, 200, "Vendor name");
        var existing = await db.Vendors.FirstOrDefaultAsync(x => x.NormalizedName == Normalize(trimmed));
        if (existing is not null)
            return existing.IsActive ? existing : throw new InvalidOperationException($"Vendor '{trimmed}' is inactive.");

        var vendor = new Vendor { Name = trimmed, IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.Vendors.Add(vendor);
        try
        {
            await db.SaveChangesAsync();
            return vendor;
        }
        catch (DbUpdateException)
        {
            db.Entry(vendor).State = EntityState.Detached;
            var winner = await db.Vendors.SingleAsync(x => x.NormalizedName == Normalize(trimmed));
            return winner.IsActive ? winner : throw new InvalidOperationException($"Vendor '{trimmed}' is inactive.");
        }
    }

    public async Task<VendorPart?> FindOrCreateActivePartAsync(long vendorId, string? partNumber, int executedBy)
    {
        if (string.IsNullOrWhiteSpace(partNumber))
            return null;
        var trimmed = RequireTrimmed(partNumber, 100, "Vendor part number");
        var vendor = await db.Vendors.FindAsync(vendorId)
            ?? throw new InvalidOperationException($"Vendor {vendorId} does not exist.");
        if (!vendor.IsActive)
            throw new InvalidOperationException($"Vendor {vendorId} is inactive.");

        var existing = await db.VendorParts.FirstOrDefaultAsync(x => x.VendorId == vendorId && x.NormalizedPartNumber == Normalize(trimmed));
        if (existing is not null)
            return existing.IsActive ? existing : throw new InvalidOperationException($"Vendor part '{trimmed}' is inactive.");

        var part = new VendorPart { VendorId = vendorId, PartNumber = trimmed, IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.VendorParts.Add(part);
        try
        {
            await db.SaveChangesAsync();
            return part;
        }
        catch (DbUpdateException)
        {
            db.Entry(part).State = EntityState.Detached;
            var winner = await db.VendorParts.SingleAsync(x => x.VendorId == vendorId && x.NormalizedPartNumber == Normalize(trimmed));
            return winner.IsActive ? winner : throw new InvalidOperationException($"Vendor part '{trimmed}' is inactive.");
        }
    }

    private async Task<Vendor> FindRecordedVendorAsync(string? resultSummary)
    {
        if (!long.TryParse(resultSummary, out var vendorId))
            throw new InvalidOperationException("Vendor create replay has an invalid result.");
        return await db.Vendors.FindAsync(vendorId)
            ?? throw new InvalidOperationException("Vendor create replay references a missing vendor.");
    }

    private async Task<VendorPart> FindRecordedVendorPartAsync(string? resultSummary)
    {
        if (!long.TryParse(resultSummary, out var vendorPartId))
            throw new InvalidOperationException("Vendor-part create replay has an invalid result.");
        return await db.VendorParts.FindAsync(vendorPartId)
            ?? throw new InvalidOperationException("Vendor-part create replay references a missing vendor part.");
    }

    private static string RequireTrimmed(string value, int maxLength, string label)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new ArgumentException($"{label} is required.", nameof(value));
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{label} cannot exceed {maxLength} characters.", nameof(value));
        return trimmed;
    }

    private static void ValidateOptional(string? value, int maxLength, string label)
    {
        if (value?.Trim().Length > maxLength)
            throw new ArgumentException($"{label} cannot exceed {maxLength} characters.", nameof(value));
    }

    private static string? TrimOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static string Hash<T>(T request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
}
