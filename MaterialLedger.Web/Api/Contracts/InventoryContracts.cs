using MaterialLedger.Data.Models;

namespace MaterialLedger.Web.Api.Contracts;

public sealed record InventoryItemSummaryContract(
    long Id,
    string PublicItemCode,
    string MaterialSpecDisplayName,
    string MaterialDescription,
    decimal ThicknessIn,
    string? GaugeLabel,
    InventoryItemKind ItemKind,
    InventoryItemStatus Status,
    decimal QuantityOnHand,
    decimal AvailableQuantity,
    decimal? UsableWidthIn,
    decimal? UsableLengthIn,
    string LocationCode,
    DateTime LastObservedAtUtc,
    string LastObservedByDisplayName,
    bool IsStale);

public sealed record DropInventoryItemDetailContract(
    long Id,
    string PublicItemCode,
    string MaterialSpecDisplayName,
    string MaterialDescription,
    decimal ThicknessIn,
    string? GaugeLabel,
    InventoryItemKind ItemKind,
    InventoryItemStatus Status,
    decimal QuantityOnHand,
    decimal AvailableQuantity,
    decimal? UsableWidthIn,
    decimal? UsableLengthIn,
    string LocationCode,
    DateTime LastObservedAtUtc,
    string LastObservedByDisplayName,
    bool IsStale,
    InventoryItemSummaryContract? SourceSheet);
