using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Locations;
using MaterialLedger.Data.Reservations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Api;

public sealed class ApiExceptionHandler(RequestDelegate next, ILogger<ApiExceptionHandler> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        try
        {
            await next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var problem = exception switch
            {
                OperationConflictException => ApiProblemDetails.Create(
                    context, "idempotency-conflict", StatusCodes.Status409Conflict,
                    "The idempotency key was already used with a different request."),
                OperationOwnershipException => ApiProblemDetails.Create(
                    context, "idempotency-key-ownership", StatusCodes.Status403Forbidden,
                    "The idempotency key belongs to another user."),
                ReservationConcurrencyException or DbUpdateConcurrencyException => ApiProblemDetails.Create(
                    context, "concurrency-conflict", StatusCodes.Status409Conflict,
                    "The resource was changed by another operation. Refresh and try again."),
                ReservationBalanceException or LocationSlotConflictException or InvalidOperationException => ApiProblemDetails.Create(
                    context, "domain-conflict", StatusCodes.Status409Conflict,
                    "The requested operation conflicts with the current resource state.",
                    "The requested operation conflicts with the current resource state."),
                ArgumentException or FormatException or BadHttpRequestException => ApiProblemDetails.Validation(
                    context, "One or more request values are invalid."),
                _ => UnexpectedProblem(context, exception)
            };

            await ApiProblemDetails.WriteAsync(context, problem);
        }
    }

    private ProblemDetails UnexpectedProblem(HttpContext context, Exception exception)
    {
        logger.LogError(exception, "Unhandled exception while processing API request {RequestPath}", context.Request.Path);
        return ApiProblemDetails.Create(
            context, "internal-error", StatusCodes.Status500InternalServerError,
            "An unexpected error occurred.", "An unexpected error occurred while processing the request.");
    }
}
