using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MaterialLedger.Web.Api;

/// <summary>
/// API keys for other shop programs (CutSend) calling <c>/api</c>. Each key has a client name so
/// the logs say who called. Kept apart from the cookie login: a key never opens the Razor pages,
/// and a browser session never opens the API.
/// </summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "Api";

    /// <summary>Client name to key, e.g. <c>Api:Keys:CutSend</c>. Blank keys are ignored.</summary>
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The client whose key matches, compared in constant time; null when none does.</summary>
    public string? FindClient(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
            return null;
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        string? match = null;
        foreach (var (client, key) in Keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (CryptographicOperations.FixedTimeEquals(presentedBytes, Encoding.UTF8.GetBytes(key)))
                match ??= client;
        }
        return match;
    }
}

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<ApiKeyOptions> keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string PolicyName = "ApiClient";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
            return Task.FromResult(AuthenticateResult.NoResult());

        var client = keys.CurrentValue.FindClient(values.ToString());
        if (client is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, client)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
