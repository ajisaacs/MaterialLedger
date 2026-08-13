using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using MaterialLedger.Data;
using MaterialLedger.Web.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MaterialLedger.Tests.Api;

public class ApiProblemResponseTests : IClassFixture<ApiProblemResponseTests.ApiFactory>
{
    private readonly ApiFactory factory;

    public ApiProblemResponseTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Unauthenticated_api_request_returns_json_401_not_login_redirect()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/v1/_test/ping");

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthenticated", "/api/v1/_test/ping");
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Authenticated_user_without_required_role_gets_json_403()
    {
        using var client = CreateRoleClient("Programmer");

        var response = await client.GetAsync("/api/v1/_test/manager");

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", "/api/v1/_test/manager");
    }

    [Fact]
    public async Task Unknown_api_route_returns_json_404()
    {
        using var client = CreateRoleClient("Manager");

        var response = await client.GetAsync("/api/v1/no-such-route");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found", "/api/v1/no-such-route");
    }

    [Fact]
    public async Task Malformed_binding_returns_json_400()
    {
        using var client = CreateRoleClient("Manager");

        var response = await client.GetAsync("/api/v1/_test/binding?requiredValue=not-an-integer");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation", "/api/v1/_test/binding");
    }

    [Fact]
    public async Task Unexpected_endpoint_exception_returns_sanitized_json_500()
    {
        using var client = CreateRoleClient("Manager");

        var response = await client.GetAsync("/api/v1/_test/unexpected");

        var problem = await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal-error", "/api/v1/_test/unexpected");
        var serialized = problem.GetRawText();
        Assert.DoesNotContain("Test exception", serialized);
        Assert.DoesNotContain("/secret/storage", serialized);
    }

    [Theory]
    [InlineData("/api/v1/_test/domain-conflict", "domain-conflict")]
    [InlineData("/api/v1/_test/concurrency-conflict", "concurrency-conflict")]
    [InlineData("/api/v1/_test/idempotency-conflict", "idempotency-conflict")]
    public async Task Domain_and_idempotency_exceptions_have_distinct_conflict_types(string path, string typeSuffix)
    {
        using var client = CreateRoleClient("Manager");

        var response = await client.GetAsync(path);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, typeSuffix, path);
    }

    [Fact]
    public async Task Razor_page_unauthenticated_request_still_redirects_to_login()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Account");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("/Login", response.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET", "/api/v1/_test/method-not-allowed", "method-not-allowed")]
    public async Task Empty_api_error_responses_keep_a_matching_status_and_problem_type(string method, string path, string typeSuffix)
    {
        using var client = CreateRoleClient("Manager");

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        await AssertProblemAsync(response, HttpStatusCode.MethodNotAllowed, typeSuffix, path);
    }

    [Theory]
    [InlineData("/api/v1/_test/ping", "unauthenticated")]
    [InlineData("/api/v1/no-such-route", "not-found")]
    public async Task Pipeline_problems_include_stable_rfc9457_fields(string path, string typeSuffix)
    {
        using var client = typeSuffix == "unauthenticated" ? factory.CreateClient() : CreateRoleClient("Manager");

        var response = await client.GetAsync(path);

        await AssertProblemAsync(response, null, typeSuffix, path);
    }

    private HttpClient CreateRoleClient(string role)
    {
        var client = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthenticationHandler.TestScheme;
                options.DefaultChallengeScheme = TestAuthenticationHandler.TestScheme;
                options.DefaultForbidScheme = TestAuthenticationHandler.TestScheme;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.TestScheme, _ => { });
        })).CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RoleHeader, role);
        return client;
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode? expectedStatus, string typeSuffix, string instance)
    {
        if (expectedStatus is not null)
            Assert.Equal(expectedStatus.Value, response.StatusCode);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"https://materialledger.local/problems/{typeSuffix}", problem.GetProperty("type").GetString());
        Assert.Equal((int)response.StatusCode, problem.GetProperty("status").GetInt32());
        Assert.Equal(instance, problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        return problem;
    }

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MaterialLedgerDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<MaterialLedgerDbContext>>();
                services.RemoveAll<MaterialLedgerDbContext>();
                services.AddDbContext<MaterialLedgerDbContext>(options =>
                    options.UseInMemoryDatabase($"api-problem-tests-{Guid.NewGuid()}"));
            });
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "Test";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers[RoleHeader].ToString();
            if (string.IsNullOrWhiteSpace(role))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-user"), new Claim(ClaimTypes.Role, role)], TestScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
