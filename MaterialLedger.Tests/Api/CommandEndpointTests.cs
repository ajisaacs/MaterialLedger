using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Receiving;
using MaterialLedger.Data.Reservations;
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

public class CommandEndpointTests : IClassFixture<CommandEndpointTests.ApiFactory>
{
    private readonly ApiFactory factory;

    public CommandEndpointTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Commands_require_authenticated_programmer_or_manager()
    {
        using var unauthenticated = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var operatorClient = CreateClient("Operator");

        var unauthenticatedResponse = await unauthenticated.PostAsJsonAsync("/api/v1/commands", new { kind = "receive-material" });
        var forbiddenResponse = await operatorClient.PostAsJsonAsync("/api/v1/commands", new { kind = "receive-material" });

        await AssertProblemAsync(unauthenticatedResponse, HttpStatusCode.Unauthorized, "unauthenticated");
        await AssertProblemAsync(forbiddenResponse, HttpStatusCode.Forbidden, "forbidden");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task Commands_reject_missing_empty_or_malformed_idempotency_key_before_dispatch(string? key)
    {
        var service = new StubReceiveMaterialService();
        using var client = CreateClient("Manager", services => services.Replace(ServiceDescriptor.Scoped<IReceiveMaterialService>(_ => service)));
        if (key is not null)
            client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        var response = await client.PostAsJsonAsync("/api/v1/commands", new { kind = "receive-material" });

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty("idempotencyKey", out _));
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Commands_reject_duplicate_idempotency_key_before_dispatch()
    {
        var service = new StubReceiveMaterialService();
        using var client = CreateClient("Manager", services => services.Replace(ServiceDescriptor.Scoped<IReceiveMaterialService>(_ => service)));
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/v1/commands", new { kind = "receive-material" });

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty("idempotencyKey", out _));
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Invalid_idempotency_key_is_rejected_before_malformed_body_binding()
    {
        using var client = CreateClient("Manager");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "not-a-guid");

        var response = await PostJsonAsync(client, "{ malformed");

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty("idempotencyKey", out _));
    }

    [Fact]
    public async Task Commands_reject_unknown_kind_with_json_keyed_validation_error()
    {
        using var client = CreateClient("Manager");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/v1/commands", new { kind = "not-a-command" });

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty("kind", out _));
    }

    [Fact]
    public async Task Receive_material_returns_created_location_and_replay_envelope()
    {
        var key = Guid.NewGuid();
        var service = new StubReceiveMaterialService();
        using var client = CreateClient("Programmer", services => services.Replace(ServiceDescriptor.Scoped<IReceiveMaterialService>(_ => service)));
        client.DefaultRequestHeaders.Add("Idempotency-Key", key.ToString());
        var body = new { kind = "receive-material", receiveMaterial = new { inventoryLocationId = 4L, materialSpecId = 5L, itemKind = "FullSheetBundle", quantity = 2, shapeClassification = "Rectangular", isQuarantined = false, vendorId = 12L, vendorPartId = 34L, purchaseOrderNumber = "PO-1" } };

        var first = await client.PostAsJsonAsync("/api/v1/commands", body);
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
        var replay = await client.PostAsJsonAsync("/api/v1/commands", body);
        var replayJson = await replay.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("/api/v1/inventory/42", first.Headers.Location?.PathAndQuery);
        Assert.Equal("receive-material", firstJson.GetProperty("kind").GetString());
        Assert.Equal(42, firstJson.GetProperty("result").GetProperty("inventoryItemId").GetInt64());
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replay").Single());
        Assert.Equal(firstJson.GetRawText(), replayJson.GetRawText());
        Assert.Equal(2, service.Calls);
        Assert.NotNull(service.LastRequest);
        Assert.Equal(12L, service.LastRequest!.VendorId);
        Assert.Equal(34L, service.LastRequest.VendorPartId);
    }

    [Fact]
    public async Task Reservation_returns_created_location_and_proposed_status()
    {
        var service = new StubReservationService();
        using var client = CreateClient("Manager", services => services.Replace(ServiceDescriptor.Scoped<IReservationService>(_ => service)));
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/v1/commands", new { kind = "create-reservation", createReservation = new { inventoryItemId = 9L, expectedRemnants = new[] { new { widthIn = 12.5m, lengthIn = 24m, sourceType = "Manual" } } } });
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/v1/reservations/99", response.Headers.Location?.PathAndQuery);
        Assert.Equal("Proposed", json.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(99, json.GetProperty("result").GetProperty("materialReservationId").GetInt64());
    }

    [Fact]
    public async Task Reservation_passes_the_opennest_nest_reference_to_the_service()
    {
        var service = new StubReservationService();
        using var client = CreateClient("Manager", services => services.Replace(ServiceDescriptor.Scoped<IReservationService>(_ => service)));
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var nestId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/api/v1/commands", new { kind = "create-reservation", createReservation = new { inventoryItemId = 9L, openNestNestId = nestId, nestName = "ON-NEST-7" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(nestId, service.LastRequest!.OpenNestNestId);
        Assert.Equal("ON-NEST-7", service.LastRequest.NestName);
    }

    [Theory]
    [InlineData("pepNestYear", "{\"kind\":\"create-reservation\",\"createReservation\":{\"inventoryItemId\":9,\"pepNestYear\":2026}}")]
    [InlineData("pepNestName", "{\"kind\":\"create-reservation\",\"createReservation\":{\"inventoryItemId\":9,\"pepNestName\":\"NEST-31\"}}")]
    public async Task Reservation_rejects_the_removed_legacy_nest_fields(string field, string body)
    {
        var reservations = new StubReservationService();
        using var client = CreateClient("Manager", services => services.Replace(ServiceDescriptor.Scoped<IReservationService>(_ => reservations)));
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await PostJsonAsync(client, body);

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(0, reservations.Calls);
    }

    [Theory]
    [InlineData("{\"kind\":\"create-reservation\",\"createReservation\":{\"inventoryItemId\":9,\"openNestNestId\":\"6f9a1c1e-3c55-4d7e-9b0e-2a4f1d2c3b4a\"}}", "nestName")]
    [InlineData("{\"kind\":\"create-reservation\",\"createReservation\":{\"inventoryItemId\":9,\"openNestNestId\":\"6f9a1c1e-3c55-4d7e-9b0e-2a4f1d2c3b4a\",\"nestName\":\"  \"}}", "nestName")]
    public async Task Reservation_rejects_an_incomplete_nest_reference(string body, string errorProperty)
    {
        var reservations = new StubReservationService();
        using var client = CreateClient("Manager", services => services.Replace(ServiceDescriptor.Scoped<IReservationService>(_ => reservations)));
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await PostJsonAsync(client, body);

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty(errorProperty, out _));
        Assert.Equal(0, reservations.Calls);
    }

    [Fact]
    public async Task Reservation_rejects_a_nest_name_longer_than_the_column()
    {
        var reservations = new StubReservationService();
        using var client = CreateClient("Manager", services => services.Replace(ServiceDescriptor.Scoped<IReservationService>(_ => reservations)));
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/v1/commands", new { kind = "create-reservation", createReservation = new { inventoryItemId = 9L, openNestNestId = Guid.NewGuid(), nestName = new string('N', 201) } });

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty("nestName", out _));
        Assert.Equal(0, reservations.Calls);
    }

    [Fact]
    public async Task Reused_key_with_changed_command_maps_service_conflict_to_idempotency_problem()
    {
        using var client = CreateClient("Manager", services => services.Replace(ServiceDescriptor.Scoped<IReceiveMaterialService>(_ => new ConflictReceiveMaterialService())));
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/v1/commands", new { kind = "receive-material", receiveMaterial = new { inventoryLocationId = 4L, materialSpecId = 5L, itemKind = "FullSheetBundle", quantity = 2, shapeClassification = "Rectangular", isQuarantined = false, purchaseOrderNumber = "PO-1" } });

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "idempotency-conflict");
    }

    [Fact]
    public async Task Reused_key_owned_by_another_user_does_not_expose_a_replay()
    {
        using var client = CreateClient("Manager", services => services.Replace(ServiceDescriptor.Scoped<IReceiveMaterialService>(_ => new OwnershipConflictReceiveMaterialService())));
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/v1/commands", new { kind = "receive-material", receiveMaterial = new { inventoryLocationId = 4L, materialSpecId = 5L, itemKind = "FullSheetBundle", quantity = 2, shapeClassification = "Rectangular", isQuarantined = false, purchaseOrderNumber = "PO-1" } });

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "idempotency-key-ownership");
        Assert.False(response.Headers.Contains("Idempotent-Replay"));
    }

    [Theory]
    [InlineData("{\"kind\":\"receive-material\",\"createReservation\":{\"inventoryItemId\":9}}", "receiveMaterial")]
    [InlineData("{\"kind\":\"receive-material\",\"receiveMaterial\":{},\"createReservation\":{\"inventoryItemId\":9}}", "receiveMaterial")]
    [InlineData("{\"kind\":\"create-reservation\",\"receiveMaterial\":{}}", "createReservation")]
    public async Task Commands_require_exactly_the_payload_selected_by_kind(string body, string errorProperty)
    {
        using var client = CreateClient("Manager");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await PostJsonAsync(client, body);

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty(errorProperty, out _));
    }

    [Theory]
    [InlineData("{\"kind\":\"receive-material\",\"receiveMaterial\":{\"inventoryLocationId\":4,\"materialSpecId\":5,\"itemKind\":99,\"quantity\":2,\"shapeClassification\":\"Rectangular\",\"isQuarantined\":false,\"purchaseOrderNumber\":\"PO-1\"}}", "itemKind")]
    [InlineData("{\"kind\":\"receive-material\",\"receiveMaterial\":{\"inventoryLocationId\":4,\"materialSpecId\":5,\"itemKind\":\"FullSheetBundle\",\"quantity\":2,\"shapeClassification\":99,\"isQuarantined\":false,\"purchaseOrderNumber\":\"PO-1\"}}", "shapeClassification")]
    [InlineData("{\"kind\":\"create-reservation\",\"createReservation\":{\"inventoryItemId\":9,\"equipment\":99}}", "equipment")]
    [InlineData("{\"kind\":\"create-reservation\",\"createReservation\":{\"inventoryItemId\":9,\"expectedRemnants\":[{\"widthIn\":1,\"lengthIn\":1,\"sourceType\":99}]}}", "expectedRemnants[0].sourceType")]
    public async Task Commands_return_keyed_validation_errors_for_invalid_enum_values(string body, string errorProperty)
    {
        var receiving = new StubReceiveMaterialService();
        var reservations = new StubReservationService();
        using var client = CreateClient("Manager", services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IReceiveMaterialService>(_ => receiving));
            services.Replace(ServiceDescriptor.Scoped<IReservationService>(_ => reservations));
        });
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await PostJsonAsync(client, body);

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty(errorProperty, out _));
        Assert.Equal(0, receiving.Calls);
        Assert.Equal(0, reservations.Calls);
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string body) =>
        client.PostAsync("/api/v1/commands", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

    private HttpClient CreateClient(string role, Action<IServiceCollection>? configureServices = null)
    {
        var testFactory = configureServices is null
            ? factory
            : factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(configureServices));
        var client = testFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RoleHeader, role);
        return client;
    }

    private sealed class StubReceiveMaterialService : IReceiveMaterialService
    {
        public int Calls { get; private set; }
        public ReceiveMaterialRequest? LastRequest { get; private set; }
        public Task<IdempotentResult<ReceiveMaterialResult>> ReceiveMaterialAsync(Guid correlationId, ReceiveMaterialRequest request, int receivedBy)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(new IdempotentResult<ReceiveMaterialResult>(
                new ReceiveMaterialResult { InventoryItemId = 42, SheetLotId = 24, PublicItemCode = "SHEET-42" }, Calls > 1));
        }
    }

    private sealed class ConflictReceiveMaterialService : IReceiveMaterialService
    {
        public Task<IdempotentResult<ReceiveMaterialResult>> ReceiveMaterialAsync(Guid correlationId, ReceiveMaterialRequest request, int receivedBy) =>
            throw new OperationConflictException(correlationId);
    }

    private sealed class OwnershipConflictReceiveMaterialService : IReceiveMaterialService
    {
        public Task<IdempotentResult<ReceiveMaterialResult>> ReceiveMaterialAsync(Guid correlationId, ReceiveMaterialRequest request, int receivedBy) =>
            throw new OperationOwnershipException(correlationId);
    }

    private sealed class StubReservationService : IReservationService
    {
        public int Calls { get; private set; }
        public CreateReservationRequest? LastRequest { get; private set; }
        public Task<IdempotentResult<ReservationResult>> CreateReservationAsync(Guid correlationId, CreateReservationRequest request, int createdBy)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(new IdempotentResult<ReservationResult>(
                new ReservationResult { MaterialReservationId = 99, Status = ReservationStatus.Proposed }, Calls > 1));
        }
        public Task<ReservationResult> ApproveReservationAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int approvedBy) => throw new NotImplementedException();
        public Task<ReservationResult> ReleaseReservationAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int releasedBy, string? notes) => throw new NotImplementedException();
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string typeSuffix)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"https://materialledger.local/problems/{typeSuffix}", problem.GetProperty("type").GetString());
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
                services.AddDbContext<MaterialLedgerDbContext>(options => options.UseInMemoryDatabase($"command-endpoint-tests-{Guid.NewGuid()}"));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.TestScheme;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.TestScheme;
                    options.DefaultForbidScheme = TestAuthenticationHandler.TestScheme;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.TestScheme, _ => { });
            });
        }
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "Test";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers[RoleHeader].ToString();
            if (string.IsNullOrWhiteSpace(role))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "test-user"), new Claim(ClaimTypes.Role, role)],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
