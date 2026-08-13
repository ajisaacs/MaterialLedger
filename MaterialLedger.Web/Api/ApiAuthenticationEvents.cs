using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace MaterialLedger.Web.Api;

public static class ApiAuthenticationEvents
{
    public static void Configure(CookieAuthenticationEvents events)
    {
        events.OnRedirectToLogin = context => IsApiRequest(context.Request)
            ? WriteChallengeAsync(context.HttpContext)
            : RedirectAsync(context.Response, context.RedirectUri);
        events.OnRedirectToAccessDenied = context => IsApiRequest(context.Request)
            ? WriteForbiddenAsync(context.HttpContext)
            : RedirectAsync(context.Response, context.RedirectUri);
    }

    public static Task WriteChallengeAsync(HttpContext httpContext) =>
        ApiProblemDetails.WriteAsync(httpContext, ApiProblemDetails.Create(
            httpContext,
            "unauthenticated",
            StatusCodes.Status401Unauthorized,
            "Authentication is required to access this resource."));

    public static Task WriteForbiddenAsync(HttpContext httpContext) =>
        ApiProblemDetails.WriteAsync(httpContext, ApiProblemDetails.Create(
            httpContext,
            "forbidden",
            StatusCodes.Status403Forbidden,
            "You are not authorized to access this resource."));

    private static bool IsApiRequest(HttpRequest request) => request.Path.StartsWithSegments("/api");

    private static Task RedirectAsync(HttpResponse response, string redirectUri)
    {
        response.Redirect(redirectUri);
        return Task.CompletedTask;
    }
}
