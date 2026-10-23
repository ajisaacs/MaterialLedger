using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Inventory;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Reservations;
using MaterialLedger.Web.Pages.Reservations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

public sealed class ReservationHoldPresentationTests
{
    [Fact]
    public void Inventory_search_invites_authorized_users_to_hold_material_for_work()
    {
        var page = ReadPage("Inventory", "Search.cshtml");

        Assert.Contains("Hold for work", page);
        Assert.DoesNotContain(">Reserve<", page);
    }

    [Fact]
    public void Hold_form_centers_the_selected_item_and_one_readable_work_reference()
    {
        var page = ReadPage("Reservations", "Create.cshtml");

        Assert.Contains("Hold material for work", page);
        Assert.Contains("Selected material", page);
        Assert.Contains("@Model.Item?.PublicItemCode", page);
        Assert.Contains("@Model.Item?.MaterialSpecDisplayName", page);
        Assert.Contains("Available quantity", page);
        Assert.Contains("DisplayFormat.Quantity(Model.Item.AvailableQuantity)", page);
        Assert.DoesNotContain("@Model.Item?.QuantityOnHand", page);
        Assert.Contains("Usable size", page);
        Assert.Contains("@Model.Item?.LocationCode", page);
        Assert.Equal(1, CountOccurrences(page, "Work reference"));
        Assert.Contains("asp-for=\"DrawingId\"", page);
    }

    [Fact]
    public void Hold_form_groups_equipment_and_optional_expected_drop_while_hiding_optional_ids_by_default()
    {
        var page = ReadPage("Reservations", "Create.cshtml");

        Assert.Contains("<legend>Equipment</legend>", page);
        Assert.Contains("<legend>Expected usable drops</legend>", page);
        Assert.Contains("Expected usable drop size", page);
        Assert.Contains("Leave the first box blank when no usable drops are expected", page);
        Assert.DoesNotContain("operator estimate", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenNest integration", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<details class=\"additional-traceability\">", page);
        Assert.Contains("<summary>Additional traceability</summary>", page);
        Assert.Contains("asp-for=\"ShopOpsJobId\"", page);
        Assert.Contains("asp-for=\"DrawingRevision\"", page);
        Assert.Contains("asp-for=\"PepNestYear\"", page);
        Assert.Contains("asp-for=\"PepNestName\"", page);
        Assert.Contains("asp-for=\"LaserProgramRecordId\"", page);
    }

    [Fact]
    public void Hold_form_adds_another_optional_dimension_input_after_a_valid_drop_size_without_a_checkbox()
    {
        var page = ReadPage("Reservations", "Create.cshtml");

        Assert.Contains("name=\"ExpectedDropSizes[@index]\"", page);
        Assert.Contains("expected-drop-template", page);
        Assert.Contains("addExpectedDropWhenValid", page);
        Assert.Contains("0.0x0.0", page);
        Assert.DoesNotContain("HasManualEstimate", page);
        Assert.DoesNotContain("add-expected-remnant", page);
        Assert.DoesNotContain("ExpectedRemnants[0].", page);
    }

    [Fact]
    public void Hold_form_labels_the_deliberately_permitted_unassigned_path()
    {
        var page = ReadPage("Reservations", "Create.cshtml");

        Assert.Contains("Unassigned material hold", page);
    }

    [Fact]
    public async Task Hold_get_uses_canonical_available_quantity_for_active_holds()
    {
        await using var db = CreateDb();
        var user = new AppUser { AppUserId = 1, Username = "holder", DisplayName = "Holder", PasswordHash = "hash" };
        var spec = new MaterialSpec { MaterialSpecId = 1, MaterialFamily = "Steel", DisplayName = "Steel", ThicknessIn = .25m, IsActive = true };
        var location = new InventoryLocation { InventoryLocationId = 1, LocationCode = "S-1", DisplayName = "Shelf 1", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        var item = new InventoryItem
        {
            InventoryItemId = 1, PublicItemCode = "SHEET-1", MaterialSpec = spec, InventoryLocation = location,
            ItemKind = InventoryItemKind.FullSheetBundle, Status = InventoryItemStatus.Available, QuantityOnHand = 7,
            ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedByUser = user
        };
        db.AddRange(item,
            new MaterialReservation { InventoryItem = item, Status = ReservationStatus.Proposed, CreatedAtUtc = DateTime.UtcNow, CreatedByUser = user },
            new MaterialReservation { InventoryItem = item, Status = ReservationStatus.Approved, CreatedAtUtc = DateTime.UtcNow, CreatedByUser = user },
            new MaterialReservation { InventoryItem = item, Status = ReservationStatus.AwaitingCloseout, CreatedAtUtc = DateTime.UtcNow, CreatedByUser = user },
            new MaterialReservation { InventoryItem = item, Status = ReservationStatus.Released, CreatedAtUtc = DateTime.UtcNow, CreatedByUser = user });
        await db.SaveChangesAsync();
        var model = NewCreateModel(db);

        var result = await model.OnGetAsync(item.InventoryItemId);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.Item);
        Assert.Equal(4m, model.Item.AvailableQuantity);
    }

    [Fact]
    public void Hold_form_carries_the_opennest_nest_reference_in_hidden_fields()
    {
        var page = ReadPage("Reservations", "Create.cshtml");
        var model = ReadPage("Reservations", "Create.cshtml.cs");

        Assert.Contains("<input type=\"hidden\" asp-for=\"OpenNestNestId\" />", page);
        Assert.Contains("<input type=\"hidden\" asp-for=\"NestName\" />", page);
        Assert.Contains("[BindProperty] public Guid? OpenNestNestId", model);
        Assert.Contains("[BindProperty] public string? NestName", model);
        Assert.Contains("OpenNestNestId: OpenNestNestId, NestName: NestName", model);
    }

    [Fact]
    public async Task Hold_get_accepts_the_opennest_nest_reference_from_the_query()
    {
        await using var db = CreateDb();
        var item = await AddAvailableSheetAsync(db);
        var model = NewCreateModel(db);
        var nestId = Guid.NewGuid();

        var result = await model.OnGetAsync(item.InventoryItemId, openNestNestId: nestId, nestName: "ON-NEST-7");

        Assert.IsType<PageResult>(result);
        Assert.Equal(nestId, model.OpenNestNestId);
        Assert.Equal("ON-NEST-7", model.NestName);
        Assert.Null(model.PepNestYear);
        Assert.Null(model.PepNestName);
    }

    [Fact]
    public async Task Hold_get_still_accepts_the_pep_nest_reference_from_the_query()
    {
        await using var db = CreateDb();
        var item = await AddAvailableSheetAsync(db);
        var model = NewCreateModel(db);

        var result = await model.OnGetAsync(item.InventoryItemId, pepNestYear: 2026, pepNestName: "NEST-31");

        Assert.IsType<PageResult>(result);
        Assert.Equal(2026, model.PepNestYear);
        Assert.Equal("NEST-31", model.PepNestName);
        Assert.Null(model.OpenNestNestId);
        Assert.Null(model.NestName);
    }

    private static async Task<InventoryItem> AddAvailableSheetAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { AppUserId = 1, Username = "holder", DisplayName = "Holder", PasswordHash = "hash" };
        var spec = new MaterialSpec { MaterialSpecId = 1, MaterialFamily = "Steel", DisplayName = "Steel", ThicknessIn = .25m, IsActive = true };
        var location = new InventoryLocation { InventoryLocationId = 1, LocationCode = "S-1", DisplayName = "Shelf 1", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
        var item = new InventoryItem
        {
            InventoryItemId = 1, PublicItemCode = "SHEET-1", MaterialSpec = spec, InventoryLocation = location,
            ItemKind = InventoryItemKind.FullSheet, Status = InventoryItemStatus.Available, QuantityOnHand = 1,
            ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedByUser = user
        };
        db.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    [Fact]
    public async Task Hold_get_missing_item_redirects_safely_to_inventory_search()
    {
        await using var db = CreateDb();
        var model = NewCreateModel(db);

        var result = await model.OnGetAsync(999);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Inventory/Search", redirect.PageName);
        Assert.Null(model.Item);
    }

    [Fact]
    public void Hold_details_use_plain_language_workflow_state_next_action_and_collapsed_audit_traceability()
    {
        var page = ReadPage("Reservations", "Details.cshtml");

        Assert.Contains("ReservationWorkflowDisplay.For(", page);
        Assert.Contains("Workflow status", page);
        Assert.Contains("Next action", page);
        Assert.Contains("Next responsible role", page);
        Assert.Contains("<details class=\"additional-traceability\">", page);
        Assert.Contains("<summary>Additional traceability</summary>", page);
        Assert.Contains("Technical status", page);
        Assert.Contains("ShopOps job ID", page);
        Assert.Contains("Program record ID", page);
    }

    private static string ReadPage(params string[] pathSegments) => File.ReadAllText(
        Path.Combine(new[] { RepositoryRoot, "MaterialLedger.Web", "Pages" }.Concat(pathSegments).ToArray()));

    private static MaterialLedgerDbContext CreateDb() => new(new DbContextOptionsBuilder<MaterialLedgerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private static CreateModel NewCreateModel(MaterialLedgerDbContext db) => new(
        new InventoryQueryService(db, 90),
        new ReservationService(db, new OperationLogGuard(db), 90))
    {
        TempData = new TempDataDictionary(new DefaultHttpContext(), new DictionaryTempDataProvider())
    };

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static string RepositoryRoot => Path.GetFullPath(
        "../../../../",
        AppContext.BaseDirectory);

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
