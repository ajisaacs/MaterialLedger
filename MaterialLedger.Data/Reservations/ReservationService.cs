// MaterialLedger.Data/Reservations/ReservationService.cs
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Reservations;

public record ExpectedRemnantRequest(decimal WidthIn, decimal LengthIn, ExpectedRemainderSource SourceType);

public record CreateReservationRequest(
    long InventoryItemId,
    string? ShopOpsJobId,
    string? DrawingId,
    string? DrawingRevision,
    ReservationEquipment? Equipment,
    int? PepNestYear,
    string? PepNestName,
    string? LaserProgramRecordId,
    decimal? ExpectedRemainderWidthIn,
    decimal? ExpectedRemainderLengthIn,
    ExpectedRemainderSource? ExpectedRemainderSourceType,
    IReadOnlyList<ExpectedRemnantRequest>? ExpectedRemnants = null);

public class ReservationResult
{
    public required long MaterialReservationId { get; init; }
    public required ReservationStatus Status { get; init; }
}

public class ReservationBalanceException(string message) : Exception(message);
public class ReservationConcurrencyException(string message) : Exception(message);

public interface IReservationService
{
    Task<ReservationResult> CreateReservationAsync(Guid correlationId, CreateReservationRequest request, int createdBy);
    Task<ReservationResult> ApproveReservationAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int approvedBy);
    Task<ReservationResult> ReleaseReservationAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int releasedBy, string? notes);
}

public class ReservationService(MaterialLedgerDbContext db, OperationLogGuard operationLog, int freshnessThresholdDays) : IReservationService
{
    private static readonly ReservationStatus[] BundleActiveStatuses =
    [
        ReservationStatus.Approved, ReservationStatus.AwaitingCloseout
    ];

    public async Task<ReservationResult> CreateReservationAsync(Guid correlationId, CreateReservationRequest request, int createdBy)
    {
        ValidateExpectedRemnants(request);
        var payloadHash = HashPayload(request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "CreateReservation", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<ReservationResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var item = await db.InventoryItems.FirstOrDefaultAsync(x => x.InventoryItemId == request.InventoryItemId)
            ?? throw new InvalidOperationException($"Inventory item {request.InventoryItemId} does not exist.");

        if (item.Status is InventoryItemStatus.Quarantined)
            throw new InvalidOperationException($"Item '{item.PublicItemCode}' is quarantined and cannot be reserved.");
        if (item.Status is InventoryItemStatus.Depleted or InventoryItemStatus.Scrapped)
            throw new InvalidOperationException($"Item '{item.PublicItemCode}' is {item.Status} and cannot be reserved.");
        if (item.Status is InventoryItemStatus.Reserved)
            throw new InvalidOperationException($"Item '{item.PublicItemCode}' is already reserved.");
        if (MaterialFreshness.IsStale(item.LastObservedAtUtc, freshnessThresholdDays))
            throw new InvalidOperationException(
                $"Item '{item.PublicItemCode}' was last observed {item.LastObservedAtUtc:u}, past the {freshnessThresholdDays}-day freshness threshold, and cannot be reserved without a fresh observation.");

        var now = DateTime.UtcNow;
        var expectedRemnants = GetExpectedRemnants(request);
        var reservation = new MaterialReservation
        {
            InventoryItemId = item.InventoryItemId,
            ShopOpsJobId = request.ShopOpsJobId,
            DrawingId = request.DrawingId,
            DrawingRevision = request.DrawingRevision,
            Equipment = request.Equipment,
            PepNestYear = request.PepNestYear,
            PepNestName = request.PepNestName,
            LaserProgramRecordId = request.LaserProgramRecordId,
            ReservedQuantity = 1,
            Status = ReservationStatus.Proposed,
            CreatedAtUtc = now,
            CreatedBy = createdBy,
            ExpectedRemainderWidthIn = expectedRemnants.FirstOrDefault()?.WidthIn,
            ExpectedRemainderLengthIn = expectedRemnants.FirstOrDefault()?.LengthIn,
            ExpectedRemainderSourceType = expectedRemnants.FirstOrDefault()?.SourceType,
            ExpectedRemnants = expectedRemnants.Select((remainder, index) => new ExpectedRemnant
            {
                Sequence = index + 1, WidthIn = remainder.WidthIn, LengthIn = remainder.LengthIn, SourceType = remainder.SourceType
            }).ToList()
        };
        db.MaterialReservations.Add(reservation);
        await db.SaveChangesAsync();

        db.MaterialTransactions.Add(new MaterialTransaction
        {
            OccurredAtUtc = now,
            RecordedAtUtc = now,
            RecordedBy = createdBy,
            TransactionType = MaterialTransactionType.Reservation,
            InventoryItemId = item.InventoryItemId,
            MaterialReservationId = reservation.MaterialReservationId,
            QuantityDelta = 0,
            SourceSystem = MaterialTransactionSourceSystem.OperatorUI
        });

        var result = new ReservationResult { MaterialReservationId = reservation.MaterialReservationId, Status = reservation.Status };
        operationLog.Record(correlationId, "CreateReservation", payloadHash, JsonSerializer.Serialize(result), createdBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    /// <summary>
    /// Creates a set of proposed holds in one serializable transaction. PEP programs need their
    /// exact sheet sizes, so the entire program is held or no hold is created.
    /// </summary>
    public async Task<IReadOnlyList<ReservationResult>> CreateReservationBatchAsync(
        Guid correlationId, IReadOnlyList<CreateReservationRequest> requests, int createdBy)
    {
        if (requests.Count == 0)
            throw new InvalidOperationException("At least one material hold is required.");
        foreach (var request in requests) ValidateExpectedRemnants(request);

        var payloadHash = HashPayload(requests);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var outcome = await operationLog.CheckAsync(correlationId, "CreateReservationBatch", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<List<ReservationResult>>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var itemIds = requests.Select(x => x.InventoryItemId).Distinct().ToList();
        var items = await db.InventoryItems.Where(x => itemIds.Contains(x.InventoryItemId))
            .ToDictionaryAsync(x => x.InventoryItemId);
        if (items.Count != itemIds.Count)
            throw new InvalidOperationException("One or more selected inventory items no longer exist.");

        foreach (var item in items.Values)
        {
            if (item.Status is InventoryItemStatus.Quarantined or InventoryItemStatus.Depleted or InventoryItemStatus.Scrapped or InventoryItemStatus.Reserved)
                throw new InvalidOperationException($"Item '{item.PublicItemCode}' is {item.Status} and cannot be reserved.");
            if (MaterialFreshness.IsStale(item.LastObservedAtUtc, freshnessThresholdDays))
                throw new InvalidOperationException($"Item '{item.PublicItemCode}' is past the {freshnessThresholdDays}-day freshness threshold and cannot be reserved without a fresh observation.");
        }

        var now = DateTime.UtcNow;
        var reservations = requests.Select(request =>
        {
            var remnants = GetExpectedRemnants(request);
            return new MaterialReservation
            {
                InventoryItemId = request.InventoryItemId, ShopOpsJobId = request.ShopOpsJobId,
                DrawingId = request.DrawingId, DrawingRevision = request.DrawingRevision,
                Equipment = request.Equipment, PepNestYear = request.PepNestYear,
                PepNestName = request.PepNestName, LaserProgramRecordId = request.LaserProgramRecordId,
                ReservedQuantity = 1, Status = ReservationStatus.Proposed, CreatedAtUtc = now, CreatedBy = createdBy,
                ExpectedRemainderWidthIn = remnants.FirstOrDefault()?.WidthIn,
                ExpectedRemainderLengthIn = remnants.FirstOrDefault()?.LengthIn,
                ExpectedRemainderSourceType = remnants.FirstOrDefault()?.SourceType,
                ExpectedRemnants = remnants.Select((remainder, index) => new ExpectedRemnant
                {
                    Sequence = index + 1, WidthIn = remainder.WidthIn, LengthIn = remainder.LengthIn, SourceType = remainder.SourceType
                }).ToList()
            };
        }).ToList();
        db.MaterialReservations.AddRange(reservations);
        await db.SaveChangesAsync();

        db.MaterialTransactions.AddRange(reservations.Select(reservation => new MaterialTransaction
        {
            OccurredAtUtc = now, RecordedAtUtc = now, RecordedBy = createdBy,
            TransactionType = MaterialTransactionType.Reservation, InventoryItemId = reservation.InventoryItemId,
            MaterialReservationId = reservation.MaterialReservationId, QuantityDelta = 0,
            SourceSystem = MaterialTransactionSourceSystem.OperatorUI
        }));
        var results = reservations.Select(reservation => new ReservationResult
        {
            MaterialReservationId = reservation.MaterialReservationId, Status = reservation.Status
        }).ToList();
        operationLog.Record(correlationId, "CreateReservationBatch", payloadHash, JsonSerializer.Serialize(results), createdBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return results;
    }

    public async Task<ReservationResult> ApproveReservationAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int approvedBy)
    {
        var payloadHash = HashPayload(new { materialReservationId, expectedRowVersion = Convert.ToHexString(expectedRowVersion) });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "ApproveReservation", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<ReservationResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var reservation = await db.MaterialReservations.FirstOrDefaultAsync(x => x.MaterialReservationId == materialReservationId)
            ?? throw new InvalidOperationException($"Reservation {materialReservationId} does not exist.");

        if (!reservation.RowVersion.SequenceEqual(expectedRowVersion))
            throw new ReservationConcurrencyException(
                $"Reservation {materialReservationId} was changed by someone else since it was loaded. Refresh and try again.");

        if (reservation.Status != ReservationStatus.Proposed)
            throw new InvalidOperationException(
                $"Reservation {materialReservationId} is {reservation.Status}, not Proposed — only a Proposed reservation can be approved.");

        var item = await db.InventoryItems.FirstAsync(x => x.InventoryItemId == reservation.InventoryItemId);

        if (item.Status != InventoryItemStatus.Available)
            throw new ReservationBalanceException(
                $"Item '{item.PublicItemCode}' is no longer available to approve this reservation against (current status: {item.Status}).");

        if (item.ItemKind is InventoryItemKind.Drop or InventoryItemKind.FullSheet)
        {
            item.Status = InventoryItemStatus.Reserved;
        }
        else
        {
            var activeCount = await db.MaterialReservations.CountAsync(r =>
                r.InventoryItemId == item.InventoryItemId && BundleActiveStatuses.Contains(r.Status));
            if (activeCount + 1 > item.QuantityOnHand)
                throw new ReservationBalanceException(
                    $"Item '{item.PublicItemCode}' has {item.QuantityOnHand - activeCount} of {item.QuantityOnHand} available — insufficient balance to approve this reservation.");
        }

        reservation.Status = ReservationStatus.Approved;
        reservation.ApprovedAtUtc = DateTime.UtcNow;
        reservation.ApprovedBy = approvedBy;

        var result = new ReservationResult { MaterialReservationId = reservation.MaterialReservationId, Status = reservation.Status };
        operationLog.Record(correlationId, "ApproveReservation", payloadHash, JsonSerializer.Serialize(result), approvedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    public async Task<ReservationResult> ReleaseReservationAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int releasedBy, string? notes)
    {
        var payloadHash = HashPayload(new { materialReservationId, expectedRowVersion = Convert.ToHexString(expectedRowVersion), notes });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "ReleaseReservation", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<ReservationResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var reservation = await db.MaterialReservations.FirstOrDefaultAsync(x => x.MaterialReservationId == materialReservationId)
            ?? throw new InvalidOperationException($"Reservation {materialReservationId} does not exist.");

        if (!reservation.RowVersion.SequenceEqual(expectedRowVersion))
            throw new ReservationConcurrencyException(
                $"Reservation {materialReservationId} was changed by someone else since it was loaded. Refresh and try again.");

        if (reservation.Status is not (ReservationStatus.Proposed or ReservationStatus.Approved))
            throw new InvalidOperationException(
                $"Reservation {materialReservationId} is {reservation.Status} — only a Proposed or Approved reservation can be released.");

        var item = await db.InventoryItems.FirstAsync(x => x.InventoryItemId == reservation.InventoryItemId);
        if (item.ItemKind is InventoryItemKind.Drop or InventoryItemKind.FullSheet && item.Status == InventoryItemStatus.Reserved)
            item.Status = InventoryItemStatus.Available;

        reservation.Status = ReservationStatus.Released;

        var now = DateTime.UtcNow;
        db.MaterialTransactions.Add(new MaterialTransaction
        {
            OccurredAtUtc = now,
            RecordedAtUtc = now,
            RecordedBy = releasedBy,
            TransactionType = MaterialTransactionType.ReservationRelease,
            InventoryItemId = item.InventoryItemId,
            MaterialReservationId = reservation.MaterialReservationId,
            QuantityDelta = 0,
            Notes = notes,
            SourceSystem = MaterialTransactionSourceSystem.OperatorUI
        });

        var result = new ReservationResult { MaterialReservationId = reservation.MaterialReservationId, Status = reservation.Status };
        operationLog.Record(correlationId, "ReleaseReservation", payloadHash, JsonSerializer.Serialize(result), releasedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static string HashPayload<T>(T payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));

    private static IReadOnlyList<ExpectedRemnantRequest> GetExpectedRemnants(CreateReservationRequest request) =>
        request.ExpectedRemnants is { Count: > 0 }
            ? request.ExpectedRemnants
            : request.ExpectedRemainderWidthIn is not null && request.ExpectedRemainderLengthIn is not null && request.ExpectedRemainderSourceType is not null
                ? [new ExpectedRemnantRequest(request.ExpectedRemainderWidthIn.Value, request.ExpectedRemainderLengthIn.Value, request.ExpectedRemainderSourceType.Value)]
                : [];

    private static void ValidateExpectedRemnants(CreateReservationRequest request)
    {
        foreach (var remnant in GetExpectedRemnants(request))
        {
            if (remnant.WidthIn <= 0 || remnant.LengthIn <= 0)
                throw new InvalidOperationException("Each expected drop requires a width and length greater than zero.");
        }
    }
}
