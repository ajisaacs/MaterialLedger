using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Closeout;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using MaterialLedger.Web.Pages.Closeout;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

public sealed class CloseoutPresentationTests
{
    [Fact]
    public void Queue_uses_cut_result_vocabulary_with_work_material_and_location_context()
    {
        var page = ReadCloseoutPage("Index.cshtml");

        Assert.Contains("<h1>Record cut result</h1>", page);
        Assert.Contains("<th>Material</th>", page);
        Assert.Contains("<th>Location</th>", page);
        Assert.Contains("<th>Work reference</th>", page);
        Assert.Contains("Mark cut started", page);
        Assert.Contains("Record cut result", page);
        Assert.DoesNotContain("Close material", page);
    }

    [Fact]
    public void Detail_uses_one_accessible_outcome_decision_form()
    {
        var page = ReadCloseoutPage("Details.cshtml");

        Assert.Contains("After the cut, what remains?", page);
        Assert.Equal(1, CountOccurrences(page, "<form"));
        Assert.Equal(4, CountOccurrences(page, "name=\"Outcome\""));
        Assert.Contains("type=\"radio\"", page);
        Assert.Equal(1, CountOccurrences(page, "type=\"submit\""));
        Assert.Contains("action=\"?handler=Closeout\"", page);
        Assert.Equal(1, CountOccurrences(page, "@Html.AntiForgeryToken()"));
    }

    [Fact]
    public void Detail_discloses_drop_measurements_only_for_the_drop_outcome()
    {
        var page = ReadCloseoutPage("Details.cshtml");

        Assert.Contains("data-outcome=\"UsableDropCreated\"", page);
        Assert.Contains("class=\"closeout-drop-fields\"", page);
        Assert.Contains("Record every usable rectangular drop", page);
        Assert.Contains("Measured usable width", page);
        Assert.Contains("Measured usable length", page);
        Assert.Contains("Destination slot", page);
        Assert.Contains(".closeout-decision:has(#outcome-drop:checked) .closeout-drop-fields", ReadAppCss());
    }

    [Fact]
    public void Detail_allows_operator_to_add_multiple_measured_drops_before_recording_the_cut_result()
    {
        var page = ReadCloseoutPage("Details.cshtml");

        Assert.Contains("name=\"Remnants[0].MeasuredUsableWidthIn\"", page);
        Assert.Contains("name=\"Remnants[0].MeasuredUsableLengthIn\"", page);
        Assert.Contains("name=\"Remnants[0].DestinationLocationId\"", page);
        Assert.Contains("id=\"add-remnant\"", page);
        Assert.Contains("Add another drop", page);
    }

    [Fact]
    public void Dispatcher_preserves_server_side_closeout_safety()
    {
        var pageModel = ReadCloseoutPage("Details.cshtml.cs");

        Assert.Contains("[BindProperty] public ProductionConsumptionOutcome? Outcome", pageModel);
        Assert.Contains("OnPostCloseoutAsync", pageModel);
        Assert.Contains("CloseoutConsumptionAsync(CorrelationId, request, userId)", pageModel);
        Assert.Contains("Convert.FromBase64String(ExpectedRowVersionBase64)", pageModel);
        Assert.Contains("RedirectToPage(\"Success\"", pageModel);
    }

    [Theory]
    [InlineData(ProductionConsumptionOutcome.AllConsumed)]
    [InlineData(ProductionConsumptionOutcome.FullSheetReturned)]
    [InlineData(ProductionConsumptionOutcome.UsableDropCreated)]
    [InlineData(ProductionConsumptionOutcome.NeedsReview)]
    public async Task Closeout_dispatcher_sends_bound_outcome_request_and_uses_success_prg(ProductionConsumptionOutcome outcome)
    {
        await using var db = CreateDb();
        var closeout = new RecordingCloseoutService();
        var model = NewDetailsModel(db, closeout);
        var correlationId = Guid.NewGuid();
        var rowVersion = new byte[] { 1, 2, 3, 4 };
        model.Status = ReservationStatus.AwaitingCloseout;
        model.Equipment = ReservationEquipment.Laser;
        model.CorrelationId = correlationId;
        model.ExpectedRowVersionBase64 = Convert.ToBase64String(rowVersion);
        model.Outcome = outcome;
        model.MeasuredUsableWidthIn = outcome == ProductionConsumptionOutcome.UsableDropCreated ? 12m : null;
        model.MeasuredUsableLengthIn = outcome == ProductionConsumptionOutcome.UsableDropCreated ? 24m : null;
        model.DestinationLocationId = outcome == ProductionConsumptionOutcome.UsableDropCreated ? 99 : null;
        model.Notes = "operator note";

        var action = await model.OnPostCloseoutAsync(42);

        var redirect = Assert.IsType<RedirectToPageResult>(action);
        Assert.Equal("Success", redirect.PageName);
        Assert.Equal(42L, redirect.RouteValues!["id"]);
        Assert.Equal(ReservationStatus.AwaitingCloseout, redirect.RouteValues["Status"]);
        Assert.Equal(ReservationEquipment.Laser, redirect.RouteValues["Equipment"]);
        Assert.Equal(correlationId, closeout.CloseoutCorrelationId);
        var request = Assert.IsType<CloseoutConsumptionRequest>(closeout.CloseoutRequest);
        Assert.Equal(42L, request.MaterialReservationId);
        Assert.Equal(rowVersion, request.ExpectedRowVersion);
        Assert.Equal(outcome, request.Outcome);
        Assert.Equal(model.MeasuredUsableWidthIn, request.MeasuredUsableWidthIn);
        Assert.Equal(model.MeasuredUsableLengthIn, request.MeasuredUsableLengthIn);
        Assert.Equal(model.DestinationLocationId, request.DestinationLocationId);
        Assert.Equal("operator note", request.Notes);
    }

    [Fact]
    public async Task Closeout_dispatcher_returns_page_with_concurrency_error()
    {
        await using var db = CreateDb();
        await AddReservationAsync(db, 42);
        var closeout = new RecordingCloseoutService
        {
            CloseoutException = new ReservationConcurrencyException("stale row version")
        };
        var model = NewDetailsModel(db, closeout);
        model.Status = ReservationStatus.AwaitingCloseout;
        model.Equipment = ReservationEquipment.Laser;
        model.CorrelationId = Guid.NewGuid();
        model.ExpectedRowVersionBase64 = Convert.ToBase64String([1, 2, 3, 4]);
        model.Outcome = ProductionConsumptionOutcome.AllConsumed;

        var action = await model.OnPostCloseoutAsync(42);

        Assert.IsType<PageResult>(action);
        Assert.Equal("stale row version", model.Error);
        Assert.NotNull(model.Reservation);
        Assert.NotEqual(Guid.Empty, model.CorrelationId);
        Assert.Equal(ReservationStatus.AwaitingCloseout, model.Status);
        Assert.Equal(ReservationEquipment.Laser, model.Equipment);
    }

    [Fact]
    public async Task Closeout_dispatcher_malformed_row_version_reloads_page_with_queue_filters()
    {
        await using var db = CreateDb();
        await AddReservationAsync(db, 42, ReservationStatus.AwaitingCloseout, ReservationEquipment.Laser);
        var closeout = new RecordingCloseoutService();
        var model = NewDetailsModel(db, closeout);
        model.Status = ReservationStatus.AwaitingCloseout;
        model.Equipment = ReservationEquipment.Laser;
        model.CorrelationId = Guid.NewGuid();
        model.ExpectedRowVersionBase64 = "not valid base64!";
        model.Outcome = ProductionConsumptionOutcome.AllConsumed;

        var action = await model.OnPostCloseoutAsync(42);

        Assert.IsType<PageResult>(action);
        Assert.NotNull(model.Error);
        Assert.NotNull(model.Reservation);
        Assert.Equal(ReservationStatus.AwaitingCloseout, model.Status);
        Assert.Equal(ReservationEquipment.Laser, model.Equipment);
        Assert.Null(closeout.CloseoutRequest);
    }

    [Fact]
    public async Task Start_job_post_preserves_queue_filters_on_success_prg()
    {
        await using var db = CreateDb();
        var closeout = new RecordingCloseoutService();
        var model = NewIndexModel(db, closeout);
        var correlationId = Guid.NewGuid();
        model.Status = ReservationStatus.Approved;
        model.Equipment = ReservationEquipment.Laser;

        var action = await model.OnPostStartJobAsync(42, correlationId, Convert.ToBase64String([5, 6, 7]));

        var redirect = Assert.IsType<RedirectToPageResult>(action);
        Assert.Equal("Index", redirect.PageName);
        Assert.Equal(ReservationStatus.Approved, redirect.RouteValues!["Status"]);
        Assert.Equal(ReservationEquipment.Laser, redirect.RouteValues["Equipment"]);
        Assert.Equal(correlationId, closeout.StartCorrelationId);
        Assert.Equal(42L, closeout.StartReservationId);
        Assert.Equal(new byte[] { 5, 6, 7 }, closeout.StartExpectedRowVersion);
    }

    [Fact]
    public async Task Start_job_post_preserves_queue_filters_when_concurrency_error_reloads_page()
    {
        await using var db = CreateDb();
        await AddReservationAsync(db, 42, ReservationStatus.Approved, ReservationEquipment.Laser);
        var closeout = new RecordingCloseoutService
        {
            StartException = new ReservationConcurrencyException("stale row version")
        };
        var model = NewIndexModel(db, closeout);
        model.Status = ReservationStatus.Approved;
        model.Equipment = ReservationEquipment.Laser;

        var action = await model.OnPostStartJobAsync(42, Guid.NewGuid(), Convert.ToBase64String([5, 6, 7]));

        Assert.IsType<PageResult>(action);
        Assert.Equal("stale row version", model.Error);
        Assert.Equal(ReservationStatus.Approved, model.Status);
        Assert.Equal(ReservationEquipment.Laser, model.Equipment);
        Assert.Single(model.Reservations);
    }

    [Fact]
    public async Task Start_job_post_malformed_row_version_reloads_queue_with_filters()
    {
        await using var db = CreateDb();
        await AddReservationAsync(db, 42, ReservationStatus.Approved, ReservationEquipment.Laser);
        var closeout = new RecordingCloseoutService();
        var model = NewIndexModel(db, closeout);
        model.Status = ReservationStatus.Approved;
        model.Equipment = ReservationEquipment.Laser;

        var action = await model.OnPostStartJobAsync(42, Guid.NewGuid(), "not valid base64!");

        Assert.IsType<PageResult>(action);
        Assert.NotNull(model.Error);
        Assert.Equal(ReservationStatus.Approved, model.Status);
        Assert.Equal(ReservationEquipment.Laser, model.Equipment);
        Assert.Single(model.Reservations);
        Assert.Null(closeout.StartExpectedRowVersion);
    }

    [Fact]
    public void Closeout_navigation_posts_and_links_queue_filters()
    {
        var index = ReadCloseoutPage("Index.cshtml");
        var details = ReadCloseoutPage("Details.cshtml");

        Assert.Contains("asp-for=\"Status\"", index);
        Assert.Contains("asp-for=\"Equipment\"", index);
        Assert.Contains("asp-route-Status=\"@Model.Status\"", index);
        Assert.Contains("asp-route-Equipment=\"@Model.Equipment\"", index);
        Assert.Contains("[BindProperty(SupportsGet = true)] public ReservationStatus? Status", ReadCloseoutPage("Details.cshtml.cs"));
        Assert.Contains("asp-route-Status=\"@Model.Status\"", details);
        Assert.Contains("asp-route-Equipment=\"@Model.Equipment\"", details);
    }

    private static MaterialLedgerDbContext CreateDb() => new(new DbContextOptionsBuilder<MaterialLedgerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private static DetailsModel NewDetailsModel(MaterialLedgerDbContext db, ICloseoutService closeout) =>
        new(db, closeout) { PageContext = NewPageContext() };

    private static IndexModel NewIndexModel(MaterialLedgerDbContext db, ICloseoutService closeout) =>
        new(db, closeout) { PageContext = NewPageContext() };

    private static PageContext NewPageContext()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "7")], "test"))
        };
        return new PageContext { HttpContext = context };
    }

    private static async Task AddReservationAsync(MaterialLedgerDbContext db, long id,
        ReservationStatus status = ReservationStatus.Approved, ReservationEquipment equipment = ReservationEquipment.Laser)
    {
        var user = new AppUser { AppUserId = 7, Username = "operator", DisplayName = "Operator", PasswordHash = "hash" };
        var spec = new MaterialSpec { MaterialSpecId = 1, MaterialFamily = "Steel", DisplayName = "Steel", ThicknessIn = 0.25m, IsActive = true };
        var location = new InventoryLocation { InventoryLocationId = 1, LocationCode = "S-1", DisplayName = "Shelf 1", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        var item = new InventoryItem
        {
            InventoryItemId = 1,
            PublicItemCode = "SHEET-1",
            MaterialSpec = spec,
            InventoryLocation = location,
            ItemKind = InventoryItemKind.FullSheet,
            Status = InventoryItemStatus.Reserved,
            QuantityOnHand = 1,
            ShapeClassification = ShapeClassification.Rectangular,
            LastObservedAtUtc = DateTime.UtcNow,
            LastObservedByUser = user
        };
        db.MaterialReservations.Add(new MaterialReservation
        {
            MaterialReservationId = id,
            InventoryItem = item,
            Status = status,
            Equipment = equipment,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUser = user
        });
        await db.SaveChangesAsync();
    }

    private sealed class RecordingCloseoutService : ICloseoutService
    {
        public Exception? StartException { get; init; }
        public Exception? CloseoutException { get; init; }
        public Guid StartCorrelationId { get; private set; }
        public long StartReservationId { get; private set; }
        public byte[]? StartExpectedRowVersion { get; private set; }
        public Guid CloseoutCorrelationId { get; private set; }
        public CloseoutConsumptionRequest? CloseoutRequest { get; private set; }

        public Task<ReservationResult> MarkReservationAwaitingCloseoutAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int actorId)
        {
            if (StartException is not null) throw StartException;
            StartCorrelationId = correlationId;
            StartReservationId = materialReservationId;
            StartExpectedRowVersion = expectedRowVersion;
            return Task.FromResult(new ReservationResult { MaterialReservationId = materialReservationId, Status = ReservationStatus.AwaitingCloseout });
        }

        public Task<CloseoutResult> CloseoutConsumptionAsync(Guid correlationId, CloseoutConsumptionRequest request, int confirmedBy)
        {
            if (CloseoutException is not null) throw CloseoutException;
            CloseoutCorrelationId = correlationId;
            CloseoutRequest = request;
            return Task.FromResult(new CloseoutResult { MaterialReservationId = request.MaterialReservationId, Status = ReservationStatus.ClosedConsumed });
        }
    }

    private static string ReadCloseoutPage(string fileName) => File.ReadAllText(
        Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Closeout", fileName));

    private static string ReadAppCss() => CssTestHelper.ReadEffectiveAppCss();

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(fragment, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += fragment.Length;
        }

        return count;
    }
}
