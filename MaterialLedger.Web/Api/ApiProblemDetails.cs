using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MaterialLedger.Web.Api;

public static class ApiProblemDetails
{
    public const string ProblemTypeBaseUri = "https://materialledger.local/problems/";

    public static ProblemDetails Create(
        HttpContext httpContext,
        string typeSuffix,
        int status,
        string title,
        string? detail = null)
    {
        var problem = new ProblemDetails
        {
            Type = $"{ProblemTypeBaseUri}{typeSuffix}",
            Title = title,
            Status = status,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        return problem;
    }

    public static ValidationProblemDetails Validation(
        HttpContext httpContext,
        string title,
        IDictionary<string, string[]>? errors = null,
        string? detail = null)
    {
        var problem = new ValidationProblemDetails(errors ?? new Dictionary<string, string[]>())
        {
            Type = $"{ProblemTypeBaseUri}validation",
            Title = title,
            Status = StatusCodes.Status400BadRequest,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        return problem;
    }

    public static IResult Validation(HttpContext context, string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, type: $"{ProblemTypeBaseUri}validation", title: "One or more request values are invalid.", instance: context.Request.Path, extensions: new Dictionary<string, object?> { ["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier });

    public static IResult CreateResult(HttpContext context, string typeSuffix, int status, string title) =>
        Results.Problem(type: $"{ProblemTypeBaseUri}{typeSuffix}", title: title, statusCode: status, instance: context.Request.Path, extensions: new Dictionary<string, object?> { ["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier });

    public static Task WriteAsync(HttpContext httpContext, ProblemDetails problem)
    {
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }
}
