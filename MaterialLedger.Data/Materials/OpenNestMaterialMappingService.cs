using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Materials;

public sealed record MapOpenNestMaterialRequest(string Material, string Grade, decimal ThicknessIn, long MaterialSpecId);

/// <summary>
/// Builds the <see cref="MaterialSpecExternalMapping.ExternalKey"/> for an OpenNest material:
/// <c>{material}|{grade}|{thickness}</c>, each text part trimmed, whitespace-collapsed and lower-cased
/// (the same normalization as OpenNest's <c>SharedListNames.Key</c>). A blank grade is an empty segment.
/// </summary>
public static partial class OpenNestMaterialKey
{
    public static string For(string material, string? grade, decimal thicknessIn)
    {
        if (string.IsNullOrWhiteSpace(material))
            throw new InvalidOperationException("An OpenNest material name is required.");
        if (thicknessIn <= 0)
            throw new InvalidOperationException("An OpenNest material thickness greater than zero is required.");

        return $"{Normalize(material)}|{Normalize(grade)}|{thicknessIn.ToString("0.####", CultureInfo.InvariantCulture)}";
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Whitespace().Replace(value.Trim(), " ").ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public sealed class OpenNestMaterialMappingService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    public async Task MapAsync(Guid correlationId, MapOpenNestMaterialRequest request, int approvedBy)
    {
        var externalKey = OpenNestMaterialKey.For(request.Material, request.Grade, request.ThicknessIn);

        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var outcome = await operationLog.CheckAsync(correlationId, "MapOpenNestMaterial", payloadHash, approvedBy);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            await transaction.CommitAsync();
            return;
        }

        var materialSpec = await db.MaterialSpecs.SingleOrDefaultAsync(x => x.MaterialSpecId == request.MaterialSpecId && x.IsActive)
            ?? throw new InvalidOperationException("Select an active Material Ledger catalog material.");

        var mapping = await db.MaterialSpecExternalMappings
            .SingleOrDefaultAsync(x => x.ExternalSystem == "OpenNest" && x.ExternalKey == externalKey);

        if (mapping is null)
        {
            mapping = new MaterialSpecExternalMapping
            {
                ExternalSystem = "OpenNest", ExternalKey = externalKey, MaterialSpecId = materialSpec.MaterialSpecId
            };
            db.MaterialSpecExternalMappings.Add(mapping);
        }
        else
        {
            mapping.MaterialSpecId = materialSpec.MaterialSpecId;
        }

        mapping.Status = ExternalMappingStatus.Approved;
        mapping.ApprovedAtUtc = DateTime.UtcNow;
        mapping.ApprovedBy = approvedBy;
        operationLog.Record(correlationId, "MapOpenNestMaterial", payloadHash, JsonSerializer.Serialize(new { ExternalKey = externalKey, request.MaterialSpecId }), approvedBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
