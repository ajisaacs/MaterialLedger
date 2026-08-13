using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Reservations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MaterialLedger.Web.Api;

public static class ApiRouteExtensions
{
    public static void UseApiProblemResponses(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            await next();

            if (!context.Request.Path.StartsWithSegments("/api") || context.Response.HasStarted)
                return;

            var originalStatus = context.Response.StatusCode;
            if (originalStatus is < 400 or >= 600 || context.Response.ContentLength.GetValueOrDefault() > 0)
                return;

            var (type, status, title) = context.Response.StatusCode switch
            {
                StatusCodes.Status400BadRequest => ("validation", StatusCodes.Status400BadRequest, "One or more request values are invalid."),
                StatusCodes.Status401Unauthorized => ("unauthenticated", StatusCodes.Status401Unauthorized, "Authentication is required to access this resource."),
                StatusCodes.Status403Forbidden => ("forbidden", StatusCodes.Status403Forbidden, "You are not authorized to access this resource."),
                StatusCodes.Status404NotFound => ("not-found", StatusCodes.Status404NotFound, "The requested resource was not found."),
                StatusCodes.Status405MethodNotAllowed => ("method-not-allowed", StatusCodes.Status405MethodNotAllowed, "The HTTP method is not allowed for this resource."),
                StatusCodes.Status409Conflict => ("conflict", StatusCodes.Status409Conflict, "The request conflicts with the current resource state."),
                _ => ("internal-error", StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
            };

            await ApiProblemDetails.WriteAsync(context, ApiProblemDetails.Create(context, type, status, title));
        });
    }

    public static RouteGroupBuilder MapMaterialLedgerApi(this IEndpointRouteBuilder endpoints, IWebHostEnvironment environment)
    {
        var api = endpoints.MapGroup("/api/v1").RequireAuthorization();

        if (environment.IsEnvironment("Testing"))
        {
            api.MapGet("/_test/ping", () => Results.NoContent());
            api.MapGet("/_test/manager", () => Results.NoContent())
                .RequireAuthorization("RequireManager");
            api.MapGet("/_test/binding", (int requiredValue) => Results.NoContent());
            api.MapGet("/_test/unexpected", () => Task.FromException<IResult>(new Exception("Test exception at /secret/storage")));
            api.MapGet("/_test/domain-conflict", () => Task.FromException<IResult>(new ReservationBalanceException("Insufficient balance")));
            api.MapGet("/_test/concurrency-conflict", () => Task.FromException<IResult>(new ReservationConcurrencyException("Stale row version")));
            api.MapGet("/_test/idempotency-conflict", () => Task.FromException<IResult>(new OperationConflictException(Guid.NewGuid())));
            api.MapGet("/_test/method-not-allowed", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        }

        api.MapFallback(async context =>
        {
            await ApiProblemDetails.WriteAsync(context, ApiProblemDetails.Create(
                context,
                "not-found",
                StatusCodes.Status404NotFound,
                "The requested resource was not found."));
        });
        return api;
    }
}
