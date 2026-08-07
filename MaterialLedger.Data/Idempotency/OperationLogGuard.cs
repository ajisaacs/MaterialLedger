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

/// <summary>
/// Provides an idempotency check/record pair backed by <see cref="OperationLog"/>. This type does
/// NOT provide atomicity on its own: <see cref="CheckAsync"/> reads with <c>AsNoTracking()</c> and
/// returns before any business work happens, so two concurrent callers using the same correlation
/// id can both observe <see cref="OperationLogOutcomeKind.Unseen"/> and both proceed — a classic
/// time-of-check-to-time-of-use (TOCTOU) race that defeats the idempotency guarantee this type
/// exists to provide.
///
/// Callers MUST wrap the full check → business-work → record sequence in a single database
/// transaction with an isolation level sufficient to prevent two concurrent transactions from both
/// reading "unseen" for the same correlation id (e.g. SERIALIZABLE, or an equivalent
/// application-level lock/unique-constraint strategy). Use <see cref="Record"/> (not
/// <see cref="RecordAsync"/>) inside such a transaction so the log row is written as part of the
/// caller's own <c>SaveChangesAsync()</c> call rather than committed separately.
/// </summary>
public class OperationLogGuard(MaterialLedgerDbContext db)
{
    /// <summary>
    /// Checks whether <paramref name="correlationId"/> has already been recorded. See the
    /// class-level remarks: this method alone does not prevent a race between concurrent callers
    /// using the same correlation id — callers are responsible for wrapping check, business work,
    /// and <see cref="Record"/> in a single properly-isolated transaction.
    /// </summary>
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

    /// <summary>
    /// Transaction-friendly primitive: stages an <see cref="OperationLog"/> row via
    /// <c>db.OperationLogs.Add(...)</c> without calling <c>SaveChangesAsync()</c>. Use this from
    /// within a caller-owned transaction so the log row commits atomically with the business work
    /// it is recording the outcome of. Callers must still call their own <c>SaveChangesAsync()</c>.
    /// </summary>
    public void Record(Guid correlationId, string procedureName, string requestPayloadHash,
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
    }

    /// <summary>
    /// Convenience wrapper for standalone use outside of a caller-owned transaction: calls
    /// <see cref="Record"/> and then commits immediately via <c>SaveChangesAsync()</c>. Prefer
    /// <see cref="Record"/> directly when the log row must commit atomically with other business
    /// work in the same transaction.
    /// </summary>
    public async Task RecordAsync(Guid correlationId, string procedureName, string requestPayloadHash,
        string resultSummary, int executedBy)
    {
        Record(correlationId, procedureName, requestPayloadHash, resultSummary, executedBy);
        await db.SaveChangesAsync();
    }
}
