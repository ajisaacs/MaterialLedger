using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Idempotency;

public enum OperationLogOutcomeKind { Unseen, Replay }

public class OperationLogOutcome
{
    public required OperationLogOutcomeKind Kind { get; init; }
    public string? ResultSummary { get; init; }

    public static OperationLogOutcome Unseen() => new() { Kind = OperationLogOutcomeKind.Unseen };
    public static OperationLogOutcome Replay(string resultSummary) =>
        new() { Kind = OperationLogOutcomeKind.Replay, ResultSummary = resultSummary };
}

public class OperationConflictException(Guid correlationId) : Exception(
    $"CorrelationId '{correlationId}' was already used with a different request payload.");

public class OperationLogGuard(MaterialLedgerDbContext db)
{
    public async Task<OperationLogOutcome> CheckAsync(Guid correlationId, string procedureName, string requestPayloadHash)
    {
        var existing = await db.OperationLogs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CorrelationId == correlationId);

        if (existing is null)
            return OperationLogOutcome.Unseen();

        if (existing.ProcedureName != procedureName || existing.RequestPayloadHash != requestPayloadHash)
            throw new OperationConflictException(correlationId);

        return OperationLogOutcome.Replay(existing.ResultSummary);
    }

    public async Task RecordAsync(Guid correlationId, string procedureName, string requestPayloadHash,
        string resultSummary, int executedBy)
    {
        db.OperationLogs.Add(new OperationLog
        {
            CorrelationId = correlationId,
            ProcedureName = procedureName,
            RequestPayloadHash = requestPayloadHash,
            ResultSummary = resultSummary,
            ExecutedAtUtc = DateTime.UtcNow,
            ExecutedBy = executedBy
        });
        await db.SaveChangesAsync();
    }
}
