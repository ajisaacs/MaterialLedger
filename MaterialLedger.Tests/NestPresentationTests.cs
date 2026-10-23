using System.Net;
using System.Security.Claims;
using System.Text;
using MaterialLedger.Data;
using MaterialLedger.Data.Census;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Materials;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using MaterialLedger.Web.Pages.Nests;
using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

public sealed class NestPresentationTests
{
    private static readonly Guid NestId = Guid.Parse("3f2b6c1e-8d4a-4f6b-9a2e-1c5d7e9f0a1b");

    [Fact]
    public void Nest_browser_exposes_status_and_search_filters_and_an_id_details_route()
    {
        var page = ReadPage("Nests", "Index.cshtml");

        Assert.Contains("View nests", page);
        Assert.Contains("<select asp-for=\"Status\">", page);
        Assert.Contains("asp-for=\"Search\"", page);
        Assert.Contains("All statuses", page);
        Assert.Contains("@foreach (var status in IndexModel.StatusOptions)", page);
        Assert.Contains("asp-page=\"Details\"", page);
        Assert.Contains("asp-route-id=\"@nest.Id\"", page);
        Assert.DoesNotContain("asp-for=\"Year\"", page);
        Assert.DoesNotContain("asp-for=\"Customer\"", page);
        Assert.DoesNotContain("asp-route-year", page);
    }

    [Fact]
    public void Nest_browser_shows_material_grade_and_thickness()
    {
        var page = ReadPage("Nests", "Index.cshtml");

        Assert.Contains("IndexModel.MaterialText(nest)", page);
        Assert.Equal("Steel A36 0.25\"", IndexModel.MaterialText(new OpenNestRecord { Material = "Steel", MaterialGrade = "A36", Thickness = 0.25m }));
        Assert.Equal("Aluminum 0.125\"", IndexModel.MaterialText(new OpenNestRecord { Material = "Aluminum", Thickness = 0.125m }));
    }

    [Fact]
    public void Nest_browser_status_options_use_the_OpenNest_wire_values()
    {
        Assert.Equal(["draft", "quote", "toBeCut", "hasBeenCut"], IndexModel.StatusOptions.Select(x => x.Value));
        Assert.Equal(["Draft", "Quote", "To Be Cut", "Has Been Cut"], IndexModel.StatusOptions.Select(x => x.Label));
        Assert.Equal("To Be Cut", IndexModel.StatusLabel("toBeCut"));
        Assert.Equal("onHold", IndexModel.StatusLabel("onHold"));
    }

    [Fact]
    public async Task Nest_browser_defaults_to_to_be_cut_newest_first_limited_to_100()
    {
        var handler = new RoutingHandler().Add("api/nests/query", HttpStatusCode.OK, $$"""{"items":[{{NestJson("toBeCut")}}],"total":1,"offset":0,"limit":100}""");
        var model = new IndexModel(CreateClient(handler));

        await model.OnGetAsync();

        Assert.Equal("toBeCut", model.Status);
        Assert.Equal("/api/nests/query?status=toBeCut&sort=dateCreated&order=desc&offset=0&limit=100", handler.Requests.Single());
        Assert.Equal("N-1001", Assert.Single(model.Nests).Name);
        Assert.Null(model.Error);
    }

    [Fact]
    public async Task Nest_browser_all_statuses_sends_only_the_search()
    {
        var handler = new RoutingHandler().Add("api/nests/query", HttpStatusCode.OK, """{"items":[],"total":0,"offset":0,"limit":100}""");
        var model = new IndexModel(CreateClient(handler)) { Status = null, Search = "acme" };

        await model.OnGetAsync();

        Assert.Equal("/api/nests/query?search=acme&sort=dateCreated&order=desc&offset=0&limit=100", handler.Requests.Single());
    }

    [Fact]
    public async Task Nest_browser_reports_when_OpenNest_cannot_be_reached()
    {
        var model = new IndexModel(CreateClient(new RoutingHandler().Add("api/nests/query", HttpStatusCode.InternalServerError, "")));

        await model.OnGetAsync();

        Assert.Equal("OpenNest nests could not be reached.", model.Error);
        Assert.Empty(model.Nests);
    }

    [Fact]
    public void Nest_details_shows_material_mapping_in_the_material_card_and_can_reserve_the_complete_program_sheet_set()
    {
        var page = ReadPage("Nests", "Details.cshtml");
        var model = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Nests", "Details.cshtml.cs"));

        Assert.StartsWith("@page \"{id:guid}\"", page);
        Assert.Contains("OpenNest material", page);
        Assert.Contains("nest-mapping-summary", page);
        Assert.Contains("nest-material-mapping-dialog", page);
        Assert.Contains("Map catalog material", page);
        Assert.Contains("Change mapping", page);
        Assert.Contains("showModal()", page);
        Assert.Contains("Catalog material", page);
        Assert.Contains("Thickness", page);
        Assert.Contains("nest-material-group", page);
        Assert.Contains("nest-material-thickness", page);
        Assert.Contains("data-material-group", page);
        Assert.Contains("updateThicknesses", page);
        Assert.Contains("Program sheet size", page);
        Assert.Contains("ProgramSheetRequirements", page);
        Assert.Contains("asp-page-handler=\"ReserveProgramSheets\"", page);
        Assert.Contains("Reserve all program sheets", page);
        Assert.Contains("creates all required holds together or creates none", page);
        Assert.Contains("nest-requirements-action", page);
        Assert.True(page.IndexOf("asp-page-handler=\"ReserveProgramSheets\"", StringComparison.Ordinal) < page.IndexOf("nest-stock-panel", StringComparison.Ordinal));
        Assert.Contains("CreateReservationBatchAsync", model);
        Assert.Contains("No holds were created", model);
        Assert.Contains("BuildReservationPlan", model);
        Assert.Contains("OnPostRecordFoundSheetAsync", model);
        Assert.Contains("RecordOpeningBalanceAsync", model);
        Assert.Contains("ProgramSheetShortfall", model);
        Assert.Contains("Found while preparing nest {Nest.Name}", model);
        Assert.Contains("OpenNestNestId == id", model);
        Assert.Contains("nest-found-sheet-dialog", page);
        Assert.Contains("Record found sheet and retry reservation", page);
        Assert.Contains("Continue inventory search", page);
        Assert.Contains("FoundSheetLocationId", page);
        Assert.Contains("ShowFoundSheetDialog", page);
        Assert.Contains("Program material holds", page);
        Assert.Contains("ProgramReservations", page);
        Assert.Contains("asp-page=\"/Reservations/Details\"", page);
        Assert.Contains("Material holds are already placed for this program", page);
        Assert.True(page.IndexOf("nest-mapping-summary", StringComparison.Ordinal) < page.IndexOf("nest-requirements-card", StringComparison.Ordinal));
        Assert.DoesNotContain("PEP", page);
    }

    [Fact]
    public void Nest_details_uses_a_dedicated_nest_workspace_layout()
    {
        var page = ReadPage("Nests", "Details.cshtml");
        var css = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "css", "nests.css"));
        var appCss = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "css", "app.css"));

        Assert.Contains("nest-workspace", page);
        Assert.Contains("nest-overview", page);
        Assert.Contains("nest-parts-panel", page);
        Assert.Contains("nest-material-card", page);
        Assert.Contains(".nest-mapping-summary", css);
        Assert.Contains(".nest-material-mapping-dialog", css);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr));", css);
        Assert.Contains(".nest-workspace", css);
        Assert.Contains(".nest-overview", css);
        Assert.Contains("@import url(\"nests.css\");", appCss);
    }

    [Fact]
    public void Nest_details_parts_table_shows_required_and_nested_counts()
    {
        var page = ReadPage("Nests", "Details.cshtml");

        Assert.Contains("<th>Part</th><th>Required</th><th>Nested</th>", page);
        Assert.Contains("@part.Required", page);
        Assert.Contains("@part.Nested", page);
        Assert.DoesNotContain("Part size", page);
    }

    [Fact]
    public void Nest_details_shows_a_warning_when_the_program_has_been_cut()
    {
        var page = ReadPage("Nests", "Details.cshtml");

        Assert.Contains("warning-banner", page);
        Assert.Contains("Model.AlreadyCut", page);
        Assert.Contains("already been cut", page);
    }

    [Fact]
    public void Nest_details_explains_and_blocks_holds_when_sheet_sizes_are_unavailable()
    {
        var page = ReadPage("Nests", "Details.cshtml");

        Assert.Contains("@if (Model.SheetsUnavailable)", page);
        Assert.Contains("OpenNest did not return sheet sizes for this nest", page);
        Assert.Contains("Model.SheetsUnavailable || Model.Mapping?.MaterialSpec is null || Model.ProgramSheetRequirements.Count == 0 ? \"disabled\" : null", page);
    }

    [Fact]
    public async Task Nest_details_returns_not_found_when_OpenNest_has_no_such_nest()
    {
        await using var db = CreateDb();
        var handler = new RoutingHandler().Add($"api/nests/{NestId}", HttpStatusCode.NotFound, "");

        var result = await NewDetailsModel(db, handler).OnGetAsync(NestId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Nest_details_shows_the_nest_without_sheets_when_the_sheets_endpoint_is_missing()
    {
        await using var db = CreateDb();
        var handler = new RoutingHandler()
            .Add($"api/nests/{NestId}/sheets", HttpStatusCode.NotFound, "")
            .Add($"api/nests/{NestId}", HttpStatusCode.OK, NestJson("hasBeenCut"));
        var model = NewDetailsModel(db, handler);

        var result = await model.OnGetAsync(NestId);

        Assert.IsType<PageResult>(result);
        Assert.Equal("N-1001", model.Nest!.Name);
        Assert.True(model.SheetsUnavailable);
        Assert.Empty(model.ProgramSheetRequirements);
        Assert.Empty(model.Parts);
        Assert.Null(model.Error);
        Assert.True(model.AlreadyCut);
    }

    [Fact]
    public async Task Nest_details_reserve_is_refused_without_sheet_sizes()
    {
        await using var db = CreateDb();
        await SeedMappingAsync(db);
        var handler = new RoutingHandler()
            .Add($"api/nests/{NestId}/sheets", HttpStatusCode.NotFound, "")
            .Add($"api/nests/{NestId}", HttpStatusCode.OK, NestJson("toBeCut"));
        var model = NewDetailsModel(db, handler);

        var result = await model.OnPostReserveProgramSheetsAsync(NestId);

        Assert.IsType<PageResult>(result);
        Assert.Equal("OpenNest did not return sheet sizes for this nest, so no holds can be created yet.", model.Error);
    }

    [Fact]
    public async Task Nest_details_finds_the_approved_OpenNest_mapping_by_material_grade_and_thickness_key()
    {
        await using var db = CreateDb();
        await SeedMappingAsync(db);
        var handler = new RoutingHandler()
            .Add($"api/nests/{NestId}/sheets", HttpStatusCode.OK, $$"""
                {"nestId":"{{NestId}}","revision":3,
                 "sheets":[{"width":60,"length":120,"quantity":1},{"width":48,"length":96,"quantity":2}],
                 "parts":[{"name":"BRACKET","required":10,"nested":8}]}
                """)
            .Add($"api/nests/{NestId}", HttpStatusCode.OK, NestJson("toBeCut", material: " steel ", grade: "a36"));
        var model = NewDetailsModel(db, handler);

        await model.OnGetAsync(NestId);

        Assert.Equal(11, model.Mapping?.MaterialSpecId);
        Assert.Equal(11, model.MaterialSpecId);
        Assert.False(model.SheetsUnavailable);
        Assert.False(model.AlreadyCut);
        Assert.Equal(
            [new ProgramSheetRequirement(48, 96, 2), new ProgramSheetRequirement(60, 120, 1)],
            model.ProgramSheetRequirements);
        var part = Assert.Single(model.Parts);
        Assert.Equal(("BRACKET", 10, 8), (part.Name, part.Required, part.Nested));
    }

    [Fact]
    public async Task Nest_details_lists_only_active_holds_for_this_OpenNest_nest()
    {
        await using var db = CreateDb();
        var user = new AppUser { AppUserId = 7, Username = "manager", DisplayName = "Manager", PasswordHash = "hash" };
        var spec = new MaterialSpec { MaterialSpecId = 1, MaterialFamily = "Steel", DisplayName = "Steel", ThicknessIn = .25m, IsActive = true };
        var location = new InventoryLocation { InventoryLocationId = 1, LocationCode = "S-1", DisplayName = "Shelf 1", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        var item = new InventoryItem { InventoryItemId = 1, PublicItemCode = "SHEET-1", MaterialSpec = spec, InventoryLocation = location, ItemKind = InventoryItemKind.FullSheet, Status = InventoryItemStatus.Available, QuantityOnHand = 1, ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedByUser = user };
        MaterialReservation Hold(long id, Guid? nestId, ReservationStatus status) => new()
        {
            MaterialReservationId = id, InventoryItem = item, OpenNestNestId = nestId, NestName = "N-1001", Status = status,
            Equipment = ReservationEquipment.Laser, CreatedAtUtc = DateTime.UtcNow, CreatedByUser = user, RowVersion = [1],
        };
        db.MaterialReservations.AddRange(
            Hold(1, NestId, ReservationStatus.Proposed),
            Hold(2, NestId, ReservationStatus.Released),
            Hold(3, Guid.NewGuid(), ReservationStatus.Proposed),
            Hold(4, null, ReservationStatus.Approved));
        await db.SaveChangesAsync();
        var handler = new RoutingHandler()
            .Add($"api/nests/{NestId}/sheets", HttpStatusCode.NotFound, "")
            .Add($"api/nests/{NestId}", HttpStatusCode.OK, NestJson("toBeCut"));
        var model = NewDetailsModel(db, handler);

        await model.OnGetAsync(NestId);

        Assert.Equal(1, Assert.Single(model.ProgramReservations).MaterialReservationId);
    }

    [Fact]
    public void Nest_holds_carry_the_OpenNest_nest_id_and_name()
    {
        var requests = DetailsModel.BuildHoldRequests(NestId, "N-1001", [5, 5, 9]);

        Assert.Equal([5L, 5L, 9L], requests.Select(x => x.InventoryItemId));
        Assert.All(requests, request =>
        {
            Assert.Equal(NestId, request.OpenNestNestId);
            Assert.Equal("N-1001", request.NestName);
        });
    }

    [Fact]
    public void Navigation_links_authorized_users_to_nests()
    {
        var layout = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Shared", "_Layout.cshtml"));
        var home = ReadPage("Index.cshtml");

        Assert.Contains("<a asp-page=\"/Nests/Index\">Nests</a>", layout);
        Assert.Contains("asp-page=\"/Nests/Index\">Nests</a>", home);
    }

    private static async Task SeedMappingAsync(MaterialLedgerDbContext db)
    {
        var steel = new MaterialSpec { MaterialSpecId = 11, MaterialFamily = "Steel", Grade = "A36", DisplayName = "Steel A36 1/4", ThicknessIn = .25m, IsActive = true };
        var decoy = new MaterialSpec { MaterialSpecId = 12, MaterialFamily = "Steel", Grade = "A36", DisplayName = "Decoy", ThicknessIn = .25m, IsActive = true };
        db.MaterialSpecs.AddRange(steel, decoy);
        var key = OpenNestMaterialKey.For("Steel", "A36", .25m);
        db.MaterialSpecExternalMappings.AddRange(
            new MaterialSpecExternalMapping { MaterialSpecExternalMappingId = 1, MaterialSpec = decoy, ExternalSystem = "Other", ExternalKey = key, Status = ExternalMappingStatus.Approved },
            new MaterialSpecExternalMapping { MaterialSpecExternalMappingId = 2, MaterialSpec = steel, ExternalSystem = "OpenNest", ExternalKey = key, Status = ExternalMappingStatus.Approved });
        await db.SaveChangesAsync();
    }

    private static DetailsModel NewDetailsModel(MaterialLedgerDbContext db, RoutingHandler handler)
    {
        var operationLog = new OperationLogGuard(db);
        return new DetailsModel(
            CreateClient(handler), db, new InventoryQueryService(db, 90), new OpenNestMaterialMappingService(db, operationLog),
            new ReservationService(db, operationLog, 90), new OpeningBalanceService(db, operationLog))
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "7"), new Claim(ClaimTypes.Role, "Manager")], "test")),
                },
            },
        };
    }

    private static MaterialLedgerDbContext CreateDb() => new(new DbContextOptionsBuilder<MaterialLedgerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static string NestJson(string status, string material = "Steel", string grade = "A36") => $$"""
        {"id":"{{NestId}}","revision":3,"deleted":false,"name":"N-1001","customer":"Acme",
         "dateCreated":"2026-10-01T08:30:00Z","dateModified":"2026-10-02T09:00:00Z",
         "material":"{{material}}","materialGrade":"{{grade}}","thickness":0.25,"status":"{{status}}",
         "plateCount":3,"partCount":10,"comments":null,"madeBy":"aj","fileSize":1,"savedAt":"2026-10-02T09:00:00Z"}
        """;

    private static OpenNestClient CreateClient(RoutingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://opennest.test/") });

    /// <summary>Answers by path prefix, first match wins, so register longer paths first.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly List<(string Path, HttpStatusCode Status, string Body)> routes = [];
        public List<string> Requests { get; } = [];

        public RoutingHandler Add(string path, HttpStatusCode status, string body)
        {
            routes.Add(("/" + path, status, body));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            Requests.Add(pathAndQuery);
            var route = routes.First(r => request.RequestUri.AbsolutePath.StartsWith(r.Path, StringComparison.Ordinal));
            return Task.FromResult(new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string ReadPage(params string[] pathSegments) => File.ReadAllText(
        Path.Combine(new[] { RepositoryRoot, "MaterialLedger.Web", "Pages" }.Concat(pathSegments).ToArray()));

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
