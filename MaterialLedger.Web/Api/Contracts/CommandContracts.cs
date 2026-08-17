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

public sealed record CreateReservationCommand(
    long? InventoryItemId,
    string? ShopOpsJobId,
    string? DrawingId,
    string? DrawingRevision,
    ReservationEquipment? Equipment,
    int? PepNestYear,
    string? PepNestName,
    string? LaserProgramRecordId,
    IReadOnlyList<ExpectedRemnantCommand>? ExpectedRemnants);

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
    int? PepNestYear,
    string? PepNestName,
    string? LaserProgramRecordId,
    DateTime CreatedAtUtc);
