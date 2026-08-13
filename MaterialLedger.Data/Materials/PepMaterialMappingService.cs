using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Materials;

public sealed record MapPepMaterialRequest(int PepMaterialNumber, long MaterialSpecId);

public sealed class PepMaterialMappingService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    public async Task MapAsync(Guid correlationId, MapPepMaterialRequest request, int approvedBy)
    {
        if (request.PepMaterialNumber <= 0)
            throw new InvalidOperationException("A valid PEP material number is required.");

        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var outcome = await operationLog.CheckAsync(correlationId, "MapPepMaterial", payloadHash, approvedBy);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            await transaction.CommitAsync();
            return;
        }

        var materialSpec = await db.MaterialSpecs.SingleOrDefaultAsync(x => x.MaterialSpecId == request.MaterialSpecId && x.IsActive)
            ?? throw new InvalidOperationException("Select an active Material Ledger catalog material.");

        var externalKey = request.PepMaterialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var mapping = await db.MaterialSpecExternalMappings
            .SingleOrDefaultAsync(x => x.ExternalSystem == "PEP" && x.ExternalKey == externalKey);

        if (mapping is null)
        {
            mapping = new MaterialSpecExternalMapping
            {
                ExternalSystem = "PEP", ExternalKey = externalKey, MaterialSpecId = materialSpec.MaterialSpecId
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
        operationLog.Record(correlationId, "MapPepMaterial", payloadHash, JsonSerializer.Serialize(new { request.PepMaterialNumber, request.MaterialSpecId }), approvedBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
