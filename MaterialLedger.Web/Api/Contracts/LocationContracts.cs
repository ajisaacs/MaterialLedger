using MaterialLedger.Data.Models;

namespace MaterialLedger.Web.Api.Contracts;

public sealed record LocationContract(
    long Id,
    long? ParentId,
    string? ParentCode,
    string Code,
    string DisplayName,
    InventoryLocationType Type,
    InventoryStorageMode StorageMode,
    bool CapacityConfirmed,
    string? Notes);
