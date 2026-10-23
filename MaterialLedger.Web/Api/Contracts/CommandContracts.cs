using System.Text.Json.Serialization;
using MaterialLedger.Data.Models;

namespace MaterialLedger.Web.Api.Contracts;

public sealed record CommandRequest(
    string? Kind,
    ReceiveMaterialCommand? ReceiveMaterial,
    CreateReservationCommand? CreateReservation);

public sealed record ReceiveMaterialCommand(
    long? InventoryLocationId,
    long? MaterialSpecId,
    InventoryItemKind? ItemKind,
    decimal? Quantity,
    decimal? UsableWidthIn,
    decimal? UsableLengthIn,
    ShapeClassification? ShapeClassification,
    bool? IsQuarantined,
    long? VendorId,
    string? PurchaseOrderNumber,
    string? ReceiptNumber,
    long? VendorPartId,
    string? HeatLotNumber,
    decimal? CostPerUnit,
    string? Notes,
    long? PurchaseOrderLineId);

// Unknown members are rejected so callers still sending removed fields get a 400 instead of a silently unreferenced hold.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateReservationCommand(
    long? InventoryItemId,
    string? ShopOpsJobId,
    string? DrawingId,
    string? DrawingRevision,
    ReservationEquipment? Equipment,
    string? LaserProgramRecordId,
    IReadOnlyList<ExpectedRemnantCommand>? ExpectedRemnants,
    Guid? OpenNestNestId = null,
    string? NestName = null);

public sealed record ExpectedRemnantCommand(
    decimal? WidthIn,
    decimal? LengthIn,
    ExpectedRemainderSource? SourceType);

public sealed record CommandEnvelope<T>(string Kind, T Result);

public sealed record ReceiveMaterialResultContract(long InventoryItemId, long SheetLotId, string PublicItemCode);

public sealed record ReservationResultContract(long MaterialReservationId, ReservationStatus Status);

public sealed record ReservationContract(
    long MaterialReservationId,
    long InventoryItemId,
    ReservationStatus Status,
    ReservationEquipment? Equipment,
    string? ShopOpsJobId,
    string? DrawingId,
    string? DrawingRevision,
    Guid? OpenNestNestId,
    string? NestName,
    string? LaserProgramRecordId,
    DateTime CreatedAtUtc);
