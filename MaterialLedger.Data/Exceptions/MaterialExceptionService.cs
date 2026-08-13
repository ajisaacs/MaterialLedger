using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Exceptions;

public record ExceptionScanResult(int OpenedCount);

public class MaterialExceptionService(MaterialLedgerDbContext db, OperationLogGuard operationLog, int freshnessThresholdDays, int closeoutOverdueThresholdDays)
{
    private static readonly ReservationStatus[] OverdueEligibleStatuses =
    [
        ReservationStatus.AwaitingCloseout
    ];

    public async Task<ExceptionScanResult> ScanAsync()
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var openDedupeKeys = (await db.MaterialExceptions
            .Where(x => x.Status == MaterialExceptionStatus.Open)
            .Select(x => x.DedupeKey)
            .ToListAsync()).ToHashSet();

        var now = DateTime.UtcNow;
        var candidates = new List<MaterialException>();

        var quarantinedItems = await db.InventoryItems
            .Where(x => x.Status == InventoryItemStatus.Quarantined)
            .ToListAsync();
        foreach (var item in quarantinedItems)
        {
            var key = $"QuarantinedMaterial:Item:{item.InventoryItemId}";
            if (openDedupeKeys.Add(key))
                candidates.Add(new MaterialException
                {
                    ExceptionType = MaterialExceptionType.QuarantinedMaterial,
                    InventoryItemId = item.InventoryItemId,
                    DedupeKey = key,
                    Status = MaterialExceptionStatus.Open,
                    OpenedAtUtc = now
                });
        }

        var activeItems = await db.InventoryItems
            .Where(x => x.Status != InventoryItemStatus.Depleted && x.Status != InventoryItemStatus.Scrapped)
            .ToListAsync();
        foreach (var item in activeItems.Where(x => MaterialFreshness.IsStale(x.LastObservedAtUtc, freshnessThresholdDays)))
        {
            var key = $"StaleObservation:Item:{item.InventoryItemId}";
            if (openDedupeKeys.Add(key))
                candidates.Add(new MaterialException
                {
                    ExceptionType = MaterialExceptionType.StaleObservation,
                    InventoryItemId = item.InventoryItemId,
                    DedupeKey = key,
                    Status = MaterialExceptionStatus.Open,
                    OpenedAtUtc = now
                });
        }

        var candidateReservations = await db.MaterialReservations
            .Where(x => OverdueEligibleStatuses.Contains(x.Status) && x.ApprovedAtUtc != null)
            .ToListAsync();
        foreach (var reservation in candidateReservations.Where(x => (now - x.ApprovedAtUtc!.Value).TotalDays > closeoutOverdueThresholdDays))
        {
            var key = $"AwaitingCloseoutOverdue:Reservation:{reservation.MaterialReservationId}";
            if (openDedupeKeys.Add(key))
                candidates.Add(new MaterialException
                {
                    ExceptionType = MaterialExceptionType.AwaitingCloseoutOverdue,
                    MaterialReservationId = reservation.MaterialReservationId,
                    DedupeKey = key,
                    Status = MaterialExceptionStatus.Open,
                    OpenedAtUtc = now
                });
        }

        if (candidates.Count > 0)
        {
            db.MaterialExceptions.AddRange(candidates);
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync();
        return new ExceptionScanResult(candidates.Count);
    }

    public Task<List<MaterialException>> GetOpenExceptionsAsync() =>
        db.MaterialExceptions
            .Where(x => x.Status == MaterialExceptionStatus.Open)
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Include(x => x.MaterialReservation)
            .Include(x => x.OpenedByUser)
            .OrderByDescending(x => x.OpenedAtUtc)
            .ToListAsync();

    public Task<List<MaterialException>> GetRecentlyResolvedAsync(int take = 50) =>
        db.MaterialExceptions
            .Where(x => x.Status == MaterialExceptionStatus.Fixed)
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Include(x => x.MaterialReservation)
            .Include(x => x.ResolvedByUser)
            .OrderByDescending(x => x.ResolvedAtUtc)
            .Take(take)
            .ToListAsync();

    public async Task<MaterialException> ResolveMaterialExceptionAsync(Guid correlationId, long materialExceptionId, string? resolutionNotes, int resolvedBy)
    {
        var payloadHash = HashPayload(new { materialExceptionId, resolutionNotes });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "ResolveMaterialException", payloadHash, resolvedBy);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replayId = JsonSerializer.Deserialize<long>(outcome.ResultSummary!);
            await transaction.CommitAsync();
            return (await db.MaterialExceptions.FindAsync(replayId))!;
        }

        var exception = await db.MaterialExceptions.FirstOrDefaultAsync(x => x.MaterialExceptionId == materialExceptionId)
            ?? throw new InvalidOperationException($"Exception {materialExceptionId} does not exist.");

        if (exception.Status != MaterialExceptionStatus.Open)
            throw new InvalidOperationException($"Exception {materialExceptionId} is already {exception.Status}.");

        exception.Status = MaterialExceptionStatus.Fixed;
        exception.ResolvedAtUtc = DateTime.UtcNow;
        exception.ResolvedBy = resolvedBy;
        exception.ResolutionNotes = resolutionNotes;

        operationLog.Record(correlationId, "ResolveMaterialException", payloadHash, JsonSerializer.Serialize(exception.MaterialExceptionId), resolvedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return exception;
    }

    private static string HashPayload<T>(T payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
}
