using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using MaterialLedger.Web.Pages.Reservations;
using MaterialLedger.Web.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ClosingWorkReference = MaterialLedger.Web.Pages.Shared.ReservationWorkReference;

namespace MaterialLedger.Tests;

public sealed class ReservationDetailsContextPresentationTests
{
    [Fact]
    public void Queue_to_details_and_details_forms_keep_active_queue_scope()
    {
        var index = ReadReservationPage("Index.cshtml");
        var details = ReadReservationPage("Details.cshtml");
        var model = ReadReservationPage("Details.cshtml.cs");

        Assert.Matches(@"asp-page=\""Details\""\s+asp-route-id=\""@row\.Reservation\.MaterialReservationId\""\s+asp-route-Status=\""@Model\.Status\""\s+asp-route-Equipment=\""@Model\.Equipment\"">Review approval", index);
        Assert.Contains("[BindProperty(SupportsGet = true)] public ReservationStatus? Status", model);
        Assert.Contains("[BindProperty(SupportsGet = true)] public ReservationEquipment? Equipment", model);
        Assert.Contains("ReservationWorkReference.Describe(reservation)", ReadReservationPage("Index.cshtml.cs"));
        Assert.Contains("ReservationWorkReference.Describe(Model.Reservation)", details);
        Assert.Equal(2, CountOccurrences(details, "asp-for=\"Status\""));
        Assert.Equal(2, CountOccurrences(details, "asp-for=\"Equipment\""));
        Assert.Contains("asp-route-Status=\"@Model.Status\"", details);
        Assert.Contains("asp-route-Equipment=\"@Model.Equipment\"", details);
    }

    [Theory]
    [InlineData("JOB-42", "DWG-9", "B", 2026, "nest-1", "Job JOB-42")]
    [InlineData(null, "DWG-9", "B", 2026, "nest-1", "Drawing DWG-9 rev B")]
    [InlineData(null, null, null, 2026, "nest-1", "Nest 2026: nest-1")]
    [InlineData(null, null, null, null, null, "Unassigned material hold")]
    public void Work_reference_uses_the_queue_priority_for_every_supported_reference_type(
        string? jobId, string? drawingId, string? revision, int? nestYear, string? nestName, string expected)
    {
        var reservation = new MaterialReservation
        {
            ShopOpsJobId = jobId,
            DrawingId = drawingId,
            DrawingRevision = revision,
            PepNestYear = nestYear,
            PepNestName = nestName
        };

        Assert.Equal(expected, ReservationWorkReference.Describe(reservation));
    }

    [Theory]
    [InlineData("JOB-42", "DWG-9", "Job JOB-42")]
    [InlineData(null, "DWG-9", "Drawing DWG-9")]
    [InlineData(null, null, "Nest ON-NEST-7")]
    public void Work_reference_shows_the_opennest_nest_name_after_job_and_drawing(string? jobId, string? drawingId, string expected)
    {
        var reservation = new MaterialReservation
        {
            ShopOpsJobId = jobId,
            DrawingId = drawingId,
            OpenNestNestId = Guid.Parse("6f9a1c1e-3c55-4d7e-9b0e-2a4f1d2c3b4a"),
            NestName = "ON-NEST-7"
        };

        Assert.Equal(expected, ReservationWorkReference.Describe(reservation));
    }

    [Fact]
    public void Closeout_work_reference_shows_the_opennest_nest_name_and_keeps_the_pep_text()
    {
        Assert.Equal("ON-NEST-7", ClosingWorkReference.Describe(new MaterialReservation { OpenNestNestId = Guid.NewGuid(), NestName = "ON-NEST-7" }));
        Assert.Equal("2026 nest-1", ClosingWorkReference.Describe(new MaterialReservation { PepNestYear = 2026, PepNestName = "nest-1" }));
    }

    [Fact]
    public void Nest_link_points_at_the_opennest_nest_page_only_for_opennest_holds()
    {
        var nestId = Guid.Parse("6f9a1c1e-3c55-4d7e-9b0e-2a4f1d2c3b4a");

        Assert.Equal("~/Nests/6f9a1c1e-3c55-4d7e-9b0e-2a4f1d2c3b4a", ReservationWorkReference.NestPath(new MaterialReservation { OpenNestNestId = nestId, NestName = "ON-NEST-7" }));
        Assert.Null(ReservationWorkReference.NestPath(new MaterialReservation { PepNestYear = 2026, PepNestName = "nest-1" }));
        Assert.Null(ReservationWorkReference.NestPath(new MaterialReservation()));
    }

    [Fact]
    public void Details_links_opennest_holds_to_the_nest_page_and_keeps_the_pep_link()
    {
        var details = ReadReservationPage("Details.cshtml");

        Assert.Contains("ReservationWorkReference.NestPath(Model.Reservation)", details);
        Assert.Contains("href=\"@Url.Content(nestPath)\"", details);
        Assert.Contains("@Model.Reservation.NestName", details);
        Assert.Contains("asp-page=\"/PepNests/Details\"", details);
    }

    [Theory]
    [InlineData("Approve")]
    [InlineData("Release")]
    public async Task Approval_handlers_preserve_queue_scope_on_success_prg(string operation)
    {
        await using var db = CreateDb();
        var service = new RecordingReservationService();
        var model = NewDetailsModel(db, service);
        model.Status = ReservationStatus.Proposed;
        model.Equipment = ReservationEquipment.Laser;
        model.CorrelationId = Guid.NewGuid();
        model.ExpectedRowVersionBase64 = Convert.ToBase64String([1, 2, 3]);

        var action = operation == "Approve"
            ? await model.OnPostApproveAsync(42)
            : await model.OnPostReleaseAsync(42);

        var redirect = Assert.IsType<RedirectToPageResult>(action);
        Assert.Equal("Details", redirect.PageName);
        Assert.Equal(42L, redirect.RouteValues!["id"]);
        Assert.Equal(ReservationStatus.Proposed, redirect.RouteValues["Status"]);
        Assert.Equal(ReservationEquipment.Laser, redirect.RouteValues["Equipment"]);
    }

    [Theory]
    [InlineData("Approve")]
    [InlineData("Release")]
    public async Task Approval_handlers_keep_queue_scope_when_malformed_row_version_reloads_page(string operation)
    {
        await using var db = CreateDb();
        await AddReservationAsync(db, 42);
        var service = new RecordingReservationService();
        var model = NewDetailsModel(db, service);
        model.Status = ReservationStatus.Proposed;
        model.Equipment = ReservationEquipment.Laser;
        model.CorrelationId = Guid.NewGuid();
        model.ExpectedRowVersionBase64 = "not valid base64!";

        var action = operation == "Approve"
            ? await model.OnPostApproveAsync(42)
            : await model.OnPostReleaseAsync(42);

        Assert.IsType<PageResult>(action);
        Assert.NotNull(model.Error);
        Assert.NotNull(model.Reservation);
        Assert.Equal(ReservationStatus.Proposed, model.Status);
        Assert.Equal(ReservationEquipment.Laser, model.Equipment);
        Assert.Equal(0, service.ApproveCalls + service.ReleaseCalls);
    }

    private static DetailsModel NewDetailsModel(MaterialLedgerDbContext db, IReservationService service) =>
        new(db, service) { PageContext = NewPageContext() };

    private static PageContext NewPageContext()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "7"), new Claim(ClaimTypes.Role, "Manager")], "test"))
        };
        return new PageContext { HttpContext = context };
    }

    private static MaterialLedgerDbContext CreateDb() => new(new DbContextOptionsBuilder<MaterialLedgerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task AddReservationAsync(MaterialLedgerDbContext db, long id)
    {
        var user = new AppUser { AppUserId = 7, Username = "manager", DisplayName = "Manager", PasswordHash = "hash" };
        var spec = new MaterialSpec { MaterialSpecId = 1, MaterialFamily = "Steel", DisplayName = "Steel", ThicknessIn = .25m, IsActive = true };
        var location = new InventoryLocation { InventoryLocationId = 1, LocationCode = "S-1", DisplayName = "Shelf 1", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        var item = new InventoryItem { InventoryItemId = 1, PublicItemCode = "SHEET-1", MaterialSpec = spec, InventoryLocation = location, ItemKind = InventoryItemKind.FullSheet, Status = InventoryItemStatus.Available, QuantityOnHand = 1, ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedByUser = user };
        db.MaterialReservations.Add(new MaterialReservation { MaterialReservationId = id, InventoryItem = item, Status = ReservationStatus.Proposed, Equipment = ReservationEquipment.Laser, CreatedAtUtc = DateTime.UtcNow, CreatedByUser = user, RowVersion = [1, 2, 3] });
        await db.SaveChangesAsync();
    }

    private sealed class RecordingReservationService : IReservationService
    {
        public int ApproveCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

        public Task<IdempotentResult<ReservationResult>> CreateReservationAsync(Guid correlationId, CreateReservationRequest request, int createdBy) => throw new NotImplementedException();
        public Task<ReservationResult> ApproveReservationAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int approvedBy)
        {
            ApproveCalls++;
            return Task.FromResult(new ReservationResult { MaterialReservationId = materialReservationId, Status = ReservationStatus.Approved });
        }
        public Task<ReservationResult> ReleaseReservationAsync(Guid correlationId, long materialReservationId, byte[] expectedRowVersion, int releasedBy, string? notes)
        {
            ReleaseCalls++;
            return Task.FromResult(new ReservationResult { MaterialReservationId = materialReservationId, Status = ReservationStatus.Released });
        }
    }

    private static string ReadReservationPage(string fileName) => File.ReadAllText(
        Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Reservations", fileName));

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
