using MaterialLedger.Data;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Materials;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Api.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Api;

public static class ReadEndpoints
{
    public static RouteGroupBuilder MapReadEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/materials", GetMaterialsAsync)
            .WithName("SearchMaterials");
        api.MapGet("/inventory", SearchInventoryAsync)
            .WithName("SearchInventory");
        api.MapGet("/inventory/{id:long}", GetInventoryItemAsync)
            .WithName("GetInventoryItem");
        api.MapGet("/reservations/{id:long}", GetReservationAsync)
            .WithName("GetReservation");
        api.MapGet("/locations", GetLocationsAsync)
            .WithName("GetLocations");
        return api;
    }

    private static async Task<IResult> GetMaterialsAsync(string? q, MaterialSpecService service)
    {
        var specs = await service.SearchAsync(q);
        return Results.Ok(specs.Select(spec => new MaterialSpecContract(
            spec.MaterialSpecId,
            spec.MaterialFamily,
            spec.Grade,
            spec.Finish,
            spec.ThicknessIn,
            spec.DisplayName,
            spec.GaugeLabel)));
    }

    private static async Task<IResult> SearchInventoryAsync(
        string? materialFamily,
        string? grade,
        string? finish,
        decimal? thicknessIn,
        InventoryItemKind? itemKind,
        decimal? minUsableWidthIn,
        decimal? minUsableLengthIn,
        string? locationCode,
        bool? availableOnly,
        InventoryQueryService service)
    {
        var filter = new InventoryFilter(
            materialFamily,
            grade,
            finish,
            thicknessIn,
            itemKind,
            minUsableWidthIn,
            minUsableLengthIn,
            locationCode,
            availableOnly ?? true);
        var items = await service.SearchAsync(filter);
        return Results.Ok(items.Select(ToContract));
    }

    private static async Task<IResult> GetInventoryItemAsync(long id, InventoryQueryService service)
    {
        var item = await service.GetItemSummaryAsync(id);
        if (item is null)
            return Results.NotFound();

        if (item.ItemKind != InventoryItemKind.Drop)
            return Results.Ok(ToContract(item));

        var source = await service.GetSourceSheetAsync(id);
        var sourceSheet = source is null ? null : ToContract(source);

        return Results.Ok(new DropInventoryItemDetailContract(
            item.InventoryItemId,
            item.PublicItemCode,
            item.MaterialSpecDisplayName,
            item.MaterialDescription,
            item.ThicknessIn,
            item.GaugeLabel,
            item.ItemKind,
            item.Status,
            item.QuantityOnHand,
            item.AvailableQuantity,
            item.UsableWidthIn,
            item.UsableLengthIn,
            item.LocationCode,
            item.LastObservedAtUtc,
            item.LastObservedByDisplayName,
            item.IsStale,
            sourceSheet));
    }

    private static async Task<IResult> GetReservationAsync(long id, MaterialLedgerDbContext db)
    {
        var reservation = await db.MaterialReservations
            .AsNoTracking()
            .Where(x => x.MaterialReservationId == id)
            .Select(x => new ReservationContract(
                x.MaterialReservationId,
                x.InventoryItemId,
                x.Status,
                x.Equipment,
                x.ShopOpsJobId,
                x.DrawingId,
                x.DrawingRevision,
                x.OpenNestNestId,
                x.NestName,
                x.LaserProgramRecordId,
                x.CreatedAtUtc))
            .FirstOrDefaultAsync();

        return reservation is null ? Results.NotFound() : Results.Ok(reservation);
    }

    private static async Task<IResult> GetLocationsAsync(MaterialLedgerDbContext db)
    {
        var locations = await db.InventoryLocations
            .AsNoTracking()
            .Where(location => location.IsActive)
            .OrderBy(location => location.SortOrder)
            .ThenBy(location => location.LocationCode)
            .Select(location => new LocationContract(
                location.InventoryLocationId,
                location.ParentInventoryLocationId,
                location.ParentInventoryLocation == null ? null : location.ParentInventoryLocation.LocationCode,
                location.LocationCode,
                location.DisplayName,
                location.LocationType,
                location.StorageMode,
                location.CapacityConfirmed,
                location.Notes))
            .ToListAsync();
        return Results.Ok(locations);
    }

    private static InventoryItemSummaryContract ToContract(InventoryItemSummary item) => new(
        item.InventoryItemId,
        item.PublicItemCode,
        item.MaterialSpecDisplayName,
        item.MaterialDescription,
        item.ThicknessIn,
        item.GaugeLabel,
        item.ItemKind,
        item.Status,
        item.QuantityOnHand,
        item.AvailableQuantity,
        item.UsableWidthIn,
        item.UsableLengthIn,
        item.LocationCode,
        item.LastObservedAtUtc,
        item.LastObservedByDisplayName,
        item.IsStale);
}
