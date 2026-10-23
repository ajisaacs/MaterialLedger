using System.Security.Claims;
using System.Text.Json;
using MaterialLedger.Data.Receiving;
using MaterialLedger.Data.Reservations;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Api.Contracts;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MaterialLedger.Web.Api;

public static class CommandEndpoints
{
    public static RouteGroupBuilder MapCommandEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/commands", ExecuteAsync)
            .RequireAuthorization(MaterialLedgerPolicies.RequireProgrammerOrManager)
            .AddEndpointFilter(IdempotencyKeyFilterAsync);
        return api;
    }

    private static async ValueTask<object?> IdempotencyKeyFilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!TryGetCorrelationId(context.HttpContext, out _, out var keyError))
            return Validation(context.HttpContext, "idempotencyKey", keyError);

        return await next(context);
    }

    private static async Task<IResult> ExecuteAsync(
        HttpContext context,
        IReceiveMaterialService receiving,
        IReservationService reservations)
    {
        CommandRequest? command;
        try
        {
            command = await context.Request.ReadFromJsonAsync<CommandRequest>();
        }
        catch (JsonException exception)
        {
            return Validation(context, JsonPropertyFromPath(exception.Path), "The request body must be valid JSON matching the command schema.");
        }
        if (command is null)
            return Validation(context, "command", "The request body is required.");

        // The endpoint filter validated this header before this handler read the body.
        TryGetCorrelationId(context, out var correlationId, out _);

        if (!int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Results.Forbid();

        return command.Kind switch
        {
            "receive-material" => await ReceiveAsync(context, command, correlationId, userId, receiving),
            "create-reservation" => await ReserveAsync(context, command, correlationId, userId, reservations),
            _ => Validation(context, "kind", "Kind must be 'receive-material' or 'create-reservation'.")
        };
    }

    private static async Task<IResult> ReceiveAsync(HttpContext context, CommandRequest command, Guid correlationId, int userId, IReceiveMaterialService service)
    {
        if (command.ReceiveMaterial is null || command.CreateReservation is not null)
            return Validation(context, "receiveMaterial", "Exactly the payload selected by kind must be supplied.");

        var payload = command.ReceiveMaterial;
        var errors = new Dictionary<string, string[]>();
        Required(payload.InventoryLocationId, "inventoryLocationId", errors);
        Required(payload.MaterialSpecId, "materialSpecId", errors);
        Required(payload.ItemKind, "itemKind", errors);
        Required(payload.Quantity, "quantity", errors);
        Required(payload.ShapeClassification, "shapeClassification", errors);
        Required(payload.IsQuarantined, "isQuarantined", errors);
        RequiredText(payload.PurchaseOrderNumber, "purchaseOrderNumber", errors);
        if (payload.Quantity is <= 0 || (payload.Quantity is not null && payload.Quantity != decimal.Truncate(payload.Quantity.Value)))
            errors["quantity"] = ["Quantity must be a whole number of at least 1."];
        if (payload.ItemKind == InventoryItemKind.Drop)
            errors["itemKind"] = ["Receive-material accepts full sheets or bundles, not drops."];
        Defined(payload.ItemKind, "itemKind", errors);
        Defined(payload.ShapeClassification, "shapeClassification", errors);
        if (errors.Count > 0) return Validation(context, errors);

        var result = await service.ReceiveMaterialAsync(correlationId, new ReceiveMaterialRequest(
            payload.InventoryLocationId!.Value, payload.MaterialSpecId!.Value, payload.ItemKind!.Value,
            payload.Quantity!.Value, payload.UsableWidthIn, payload.UsableLengthIn, payload.ShapeClassification!.Value,
            payload.IsQuarantined!.Value, payload.VendorId, payload.PurchaseOrderNumber!, payload.ReceiptNumber,
            payload.VendorPartId, payload.HeatLotNumber, payload.CostPerUnit, payload.Notes, payload.PurchaseOrderLineId), userId);
        var response = new CommandEnvelope<ReceiveMaterialResultContract>("receive-material", new(
            result.Result.InventoryItemId, result.Result.SheetLotId, result.Result.PublicItemCode));
        if (result.WasReplayed)
        {
            context.Response.Headers.Append("Idempotent-Replay", "true");
            return Results.Ok(response);
        }
        return Results.CreatedAtRoute("GetInventoryItem", new { id = result.Result.InventoryItemId }, response);
    }

    private static async Task<IResult> ReserveAsync(HttpContext context, CommandRequest command, Guid correlationId, int userId, IReservationService service)
    {
        if (command.CreateReservation is null || command.ReceiveMaterial is not null)
            return Validation(context, "createReservation", "Exactly the payload selected by kind must be supplied.");

        var payload = command.CreateReservation;
        var errors = new Dictionary<string, string[]>();
        Required(payload.InventoryItemId, "inventoryItemId", errors);
        Defined(payload.Equipment, "equipment", errors);
        if (payload.OpenNestNestId is not null && string.IsNullOrWhiteSpace(payload.NestName))
            errors["nestName"] = ["Nest name is required when openNestNestId is set."];
        else if (payload.NestName?.Length > MaterialReservation.NestNameMaxLength)
            errors["nestName"] = [$"Nest name must be {MaterialReservation.NestNameMaxLength} characters or fewer."];
        var remnants = new List<ExpectedRemnantRequest>();
        if (payload.ExpectedRemnants is not null)
        {
            for (var index = 0; index < payload.ExpectedRemnants.Count; index++)
            {
                var remnant = payload.ExpectedRemnants[index];
                if (remnant.WidthIn is null || remnant.WidthIn <= 0)
                    errors[$"expectedRemnants[{index}].widthIn"] = ["Width must be positive."];
                if (remnant.LengthIn is null || remnant.LengthIn <= 0)
                    errors[$"expectedRemnants[{index}].lengthIn"] = ["Length must be positive."];
                if (remnant.SourceType is null)
                    errors[$"expectedRemnants[{index}].sourceType"] = ["Source type is required."];
                else if (!Enum.IsDefined(remnant.SourceType.Value))
                    errors[$"expectedRemnants[{index}].sourceType"] = ["Source type is invalid."];
                if (remnant.WidthIn is > 0 && remnant.LengthIn is > 0 && remnant.SourceType is { } sourceType && Enum.IsDefined(sourceType))
                    remnants.Add(new ExpectedRemnantRequest(remnant.WidthIn.Value, remnant.LengthIn.Value, remnant.SourceType.Value));
            }
        }
        if (errors.Count > 0) return Validation(context, errors);

        var result = await service.CreateReservationAsync(correlationId, new CreateReservationRequest(
            payload.InventoryItemId!.Value, payload.ShopOpsJobId, payload.DrawingId, payload.DrawingRevision,
            payload.Equipment, payload.LaserProgramRecordId,
            null, null, null, remnants.Count == 0 ? null : remnants,
            OpenNestNestId: payload.OpenNestNestId, NestName: payload.NestName), userId);
        var response = new CommandEnvelope<ReservationResultContract>("create-reservation", new(
            result.Result.MaterialReservationId, result.Result.Status));
        if (result.WasReplayed)
        {
            context.Response.Headers.Append("Idempotent-Replay", "true");
            return Results.Ok(response);
        }
        return Results.CreatedAtRoute("GetReservation", new { id = result.Result.MaterialReservationId }, response);
    }

    private static bool TryGetCorrelationId(HttpContext context, out Guid correlationId, out string error)
    {
        var values = context.Request.Headers["Idempotency-Key"];
        if (values.Count != 1 || !Guid.TryParse(values[0], out correlationId))
        {
            correlationId = Guid.Empty;
            error = "Idempotency-Key must be supplied exactly once as a UUID.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static IResult Validation(HttpContext context, string property, string message) =>
        Validation(context, new Dictionary<string, string[]> { [property] = [message] });

    private static IResult Validation(HttpContext context, IDictionary<string, string[]> errors) =>
        Results.Json(ApiProblemDetails.Validation(context, "One or more request values are invalid.", errors), contentType: "application/problem+json", statusCode: StatusCodes.Status400BadRequest);

    private static void Required<T>(T? value, string property, IDictionary<string, string[]> errors) where T : struct
    {
        if (value is null) errors[property] = ["This property is required."];
    }

    private static void RequiredText(string? value, string property, IDictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors[property] = ["This property is required."];
    }

    private static void Defined<T>(T? value, string property, IDictionary<string, string[]> errors) where T : struct, Enum
    {
        if (value is not null && !Enum.IsDefined(value.Value))
            errors[property] = ["This property has an invalid enum value."];
    }

    private static string JsonPropertyFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "$") return "command";
        var property = path.TrimStart('$', '.');
        return property.StartsWith("receiveMaterial.", StringComparison.Ordinal)
            ? property["receiveMaterial.".Length..]
            : property.StartsWith("createReservation.", StringComparison.Ordinal)
                ? property["createReservation.".Length..]
                : property;
    }
}
