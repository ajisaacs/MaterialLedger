using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MaterialLedger.Data.Closeout;

public record CloseoutConsumptionRequest(
    long MaterialReservationId,
    byte[] ExpectedRowVersion,
    ProductionConsumptionOutcome Outcome,
    decimal? MeasuredUsableWidthIn,
    decimal? MeasuredUsableLengthIn,
    long? DestinationLocationId,
    string? Notes);

public class CloseoutResult
{
    public required long MaterialReservationId { get; init; }
    public required ReservationStatus Status { get; init; }
    public string? RemnantPublicItemCode { get; init; }
}

public class CloseoutService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    private static readonly ReservationStatus[] CloseableStatuses =
    [
        ReservationStatus.Approved, ReservationStatus.AwaitingCloseout
    ];

    private static readonly InventoryLocationType[] LeafLocationTypes =
    [
        InventoryLocationType.Shelf, InventoryLocationType.Bay, InventoryLocationType.Staging,
        InventoryLocationType.Quarantine, InventoryLocationType.Scrap
    ];

    public async Task<ReservationResult> MarkReservationAwaitingCloseoutAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int actorId)
    {
        var payloadHash = HashPayload(new { materialReservationId, expectedRowVersion = Convert.ToHexString(expectedRowVersion) });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "MarkReservationAwaitingCloseout", payloadHash);
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

        if (reservation.Status != ReservationStatus.Approved)
            throw new InvalidOperationException(
                $"Reservation {materialReservationId} is {reservation.Status}, not Approved — only an Approved reservation can be marked awaiting closeout.");

        reservation.Status = ReservationStatus.AwaitingCloseout;

        var item = await db.InventoryItems.FirstAsync(x => x.InventoryItemId == reservation.InventoryItemId);
        if (item.ItemKind is InventoryItemKind.Drop or InventoryItemKind.FullSheet)
            item.Status = InventoryItemStatus.AwaitingCloseout;

        var result = new ReservationResult { MaterialReservationId = reservation.MaterialReservationId, Status = reservation.Status };
        operationLog.Record(correlationId, "MarkReservationAwaitingCloseout", payloadHash, JsonSerializer.Serialize(result), actorId);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    public async Task<CloseoutResult> CloseoutConsumptionAsync(Guid correlationId, CloseoutConsumptionRequest request, int confirmedBy)
    {
        ValidateRequestShape(request);
        var payloadHash = HashPayload(request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "CloseoutConsumption", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<CloseoutResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var reservation = await db.MaterialReservations.FirstOrDefaultAsync(x => x.MaterialReservationId == request.MaterialReservationId)
            ?? throw new InvalidOperationException($"Reservation {request.MaterialReservationId} does not exist.");

        if (!reservation.RowVersion.SequenceEqual(request.ExpectedRowVersion))
            throw new ReservationConcurrencyException(
                $"Reservation {request.MaterialReservationId} was changed by someone else since it was loaded. Refresh and try again.");

        if (!CloseableStatuses.Contains(reservation.Status))
            throw new InvalidOperationException(
                $"Reservation {request.MaterialReservationId} is {reservation.Status} — only an Approved or AwaitingCloseout reservation can be closed out.");

        var item = await db.InventoryItems.FirstAsync(x => x.InventoryItemId == reservation.InventoryItemId);
        var now = DateTime.UtcNow;
        string? remnantCode = null;

        switch (request.Outcome)
        {
            case ProductionConsumptionOutcome.AllConsumed:
            {
                var isIndividuallyTracked = item.ItemKind is InventoryItemKind.Drop or InventoryItemKind.FullSheet;
                item.QuantityOnHand -= 1;
                if (isIndividuallyTracked)
                    item.Status = InventoryItemStatus.Depleted;

                var txn = NewTransaction(MaterialTransactionType.Consumption, item, reservation.MaterialReservationId, -1m, now, confirmedBy, request.Notes);
                db.MaterialTransactions.Add(txn);
                await db.SaveChangesAsync();

                db.ProductionConsumptions.Add(NewProductionConsumption(reservation.MaterialReservationId, txn.MaterialTransactionId, request.Outcome, 1m, now, confirmedBy, request.Notes));
                reservation.Status = ReservationStatus.ClosedConsumed;
                break;
            }
            case ProductionConsumptionOutcome.FullSheetReturned:
            {
                var isIndividuallyTracked = item.ItemKind is InventoryItemKind.Drop or InventoryItemKind.FullSheet;
                if (isIndividuallyTracked)
                    item.Status = InventoryItemStatus.Available;
                item.LastObservedAtUtc = now;
                item.LastObservedBy = confirmedBy;

                var txn = NewTransaction(MaterialTransactionType.Return, item, reservation.MaterialReservationId, 0m, now, confirmedBy, request.Notes);
                db.MaterialTransactions.Add(txn);
                await db.SaveChangesAsync();

                db.ProductionConsumptions.Add(NewProductionConsumption(reservation.MaterialReservationId, txn.MaterialTransactionId, request.Outcome, 0m, now, confirmedBy, request.Notes));
                reservation.Status = ReservationStatus.ClosedReturned;
                break;
            }
            case ProductionConsumptionOutcome.UsableDropCreated:
            {
                var destination = await db.InventoryLocations.FirstOrDefaultAsync(x => x.InventoryLocationId == request.DestinationLocationId)
                    ?? throw new InvalidOperationException($"Destination location {request.DestinationLocationId} does not exist.");
                if (!LeafLocationTypes.Contains(destination.LocationType))
                    throw new InvalidOperationException(
                        $"Destination location '{destination.LocationCode}' is a {destination.LocationType} — a drop can only be placed at a leaf location.");

                var isIndividuallyTracked = item.ItemKind is InventoryItemKind.Drop or InventoryItemKind.FullSheet;
                item.QuantityOnHand -= 1;
                if (isIndividuallyTracked)
                    item.Status = InventoryItemStatus.Depleted;

                var consumeTxn = NewTransaction(MaterialTransactionType.Consumption, item, reservation.MaterialReservationId, -1m, now, confirmedBy, request.Notes);
                db.MaterialTransactions.Add(consumeTxn);
                await db.SaveChangesAsync();

                long nextSeq;
                await using (var command = db.Database.GetDbConnection().CreateCommand())
                {
                    command.CommandText = "SELECT NEXT VALUE FOR material.ItemCodeSeq";
                    command.Transaction = (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
                    nextSeq = (long)(await command.ExecuteScalarAsync())!;
                }
                remnantCode = $"DROP-{nextSeq}";
                var remnant = new InventoryItem
                {
                    PublicItemCode = remnantCode,
                    MaterialSpecId = item.MaterialSpecId,
                    SheetLotId = item.SheetLotId,
                    InventoryLocationId = destination.InventoryLocationId,
                    ItemKind = InventoryItemKind.Drop,
                    Status = InventoryItemStatus.Available,
                    QuantityOnHand = 1,
                    UsableWidthIn = request.MeasuredUsableWidthIn,
                    UsableLengthIn = request.MeasuredUsableLengthIn,
                    ShapeClassification = ShapeClassification.Rectangular,
                    LastObservedAtUtc = now,
                    LastObservedBy = confirmedBy,
                    Notes = request.Notes
                };
                db.InventoryItems.Add(remnant);
                await db.SaveChangesAsync();

                db.MaterialTransactions.Add(new MaterialTransaction
                {
                    OccurredAtUtc = now,
                    RecordedAtUtc = now,
                    RecordedBy = confirmedBy,
                    TransactionType = MaterialTransactionType.RemnantCreated,
                    InventoryItemId = remnant.InventoryItemId,
                    RelatedInventoryItemId = item.InventoryItemId,
                    MaterialReservationId = reservation.MaterialReservationId,
                    SheetLotId = item.SheetLotId,
                    QuantityDelta = 1m,
                    GeometryWidthInSnapshot = request.MeasuredUsableWidthIn,
                    GeometryLengthInSnapshot = request.MeasuredUsableLengthIn,
                    Notes = request.Notes,
                    SourceSystem = MaterialTransactionSourceSystem.OperatorUI
                });
                await db.SaveChangesAsync();

                db.ProductionConsumptions.Add(NewProductionConsumption(reservation.MaterialReservationId, consumeTxn.MaterialTransactionId, request.Outcome, 1m, now, confirmedBy, request.Notes));
                reservation.Status = ReservationStatus.ClosedConsumed;
                break;
            }
            case ProductionConsumptionOutcome.NeedsReview:
            {
                item.Status = InventoryItemStatus.Quarantined;

                var txn = NewTransaction(MaterialTransactionType.Quarantine, item, reservation.MaterialReservationId, 0m, now, confirmedBy, request.Notes);
                db.MaterialTransactions.Add(txn);
                await db.SaveChangesAsync();

                db.ProductionConsumptions.Add(NewProductionConsumption(reservation.MaterialReservationId, txn.MaterialTransactionId, request.Outcome, null, now, confirmedBy, request.Notes));
                reservation.Status = ReservationStatus.Exception;

                var dedupeKey = $"OutcomeUnknown:Reservation:{reservation.MaterialReservationId}";
                var alreadyOpen = await db.MaterialExceptions.AnyAsync(x => x.DedupeKey == dedupeKey && x.Status == MaterialExceptionStatus.Open);
                if (!alreadyOpen)
                {
                    db.MaterialExceptions.Add(new MaterialException
                    {
                        ExceptionType = MaterialExceptionType.OutcomeUnknown,
                        InventoryItemId = item.InventoryItemId,
                        MaterialReservationId = reservation.MaterialReservationId,
                        DedupeKey = dedupeKey,
                        Status = MaterialExceptionStatus.Open,
                        OpenedAtUtc = now,
                        OpenedBy = confirmedBy,
                        Notes = request.Notes
                    });
                }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(request), $"Unknown outcome {request.Outcome}.");
        }

        var result = new CloseoutResult { MaterialReservationId = reservation.MaterialReservationId, Status = reservation.Status, RemnantPublicItemCode = remnantCode };
        operationLog.Record(correlationId, "CloseoutConsumption", payloadHash, JsonSerializer.Serialize(result), confirmedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static MaterialTransaction NewTransaction(MaterialTransactionType type, InventoryItem item, long reservationId, decimal quantityDelta, DateTime now, int actorId, string? notes) => new()
    {
        OccurredAtUtc = now,
        RecordedAtUtc = now,
        RecordedBy = actorId,
        TransactionType = type,
        InventoryItemId = item.InventoryItemId,
        MaterialReservationId = reservationId,
        SheetLotId = item.SheetLotId,
        QuantityDelta = quantityDelta,
        Notes = notes,
        SourceSystem = MaterialTransactionSourceSystem.OperatorUI
    };

    private static ProductionConsumption NewProductionConsumption(long reservationId, long primaryTransactionId, ProductionConsumptionOutcome outcome, decimal? actualConsumedQuantity, DateTime now, int actorId, string? notes) => new()
    {
        MaterialReservationId = reservationId,
        PrimaryMaterialTransactionId = primaryTransactionId,
        ConfirmedAtUtc = now,
        ConfirmedBy = actorId,
        Outcome = outcome,
        ActualConsumedQuantity = actualConsumedQuantity,
        Notes = notes
    };

    private static void ValidateRequestShape(CloseoutConsumptionRequest request)
    {
        if (request.Outcome != ProductionConsumptionOutcome.UsableDropCreated)
            return;
        if (request.DestinationLocationId is null)
            throw new ArgumentException("A destination slot is required when recording a usable drop.", nameof(request));
        if (request.MeasuredUsableWidthIn is null or <= 0 || request.MeasuredUsableLengthIn is null or <= 0)
            throw new ArgumentException("A usable drop requires a measured width and length greater than zero.", nameof(request));
    }

    private static string HashPayload<T>(T payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
}
