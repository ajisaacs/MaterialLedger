using Xunit;

namespace MaterialLedger.Tests;

public sealed class QuickSheetEntryPresentationTests
{
    [Fact]
    public void Control_board_offers_a_modal_for_recording_one_sheet_at_a_confirmed_location()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Index.cshtml"));

        Assert.Contains("Add a sheet", page);
        Assert.Contains("<dialog id=\"add-sheet-dialog\"", page);
        Assert.Contains("method=\"dialog\"", page);
        Assert.Contains("action=\"?handler=AddSheet\"", page);
        Assert.Contains("asp-for=\"NewSheet.LocationId\"", page);
        Assert.Contains("Model.MaterialLocations", page);
        Assert.Contains("Material family / grade / finish", page);
        Assert.Contains("Thickness", page);
        Assert.Contains("data-material-key", page);
        Assert.Contains("@spec.DisplayName", page);
        Assert.Contains("asp-for=\"NewSheet.ExistingMaterialSpecId\"", page);
        Assert.Contains("Materials/Index", page);
        Assert.DoesNotContain("asp-for=\"NewSheet.MaterialFamily\"", page);
        Assert.DoesNotContain("asp-for=\"NewSheet.ThicknessIn\"", page);
        Assert.Contains("asp-for=\"NewSheet.UsableWidthIn\"", page);
        Assert.Contains("asp-for=\"NewSheet.UsableLengthIn\"", page);
    }

    [Fact]
    public void Control_board_model_loads_only_active_material_slots_for_the_new_sheet_location_dropdown()
    {
        var pageModel = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Index.cshtml.cs"));

        Assert.Contains("ActiveMaterialSlotQuery.Create(db)", pageModel);
        Assert.Contains("MaterialLocations", pageModel);
    }

    [Fact]
    public void Add_sheet_dialog_splits_location_into_a_rack_select_and_a_dependent_slot_select()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Index.cshtml"));

        Assert.Contains("id=\"new-sheet-rack\"", page);
        Assert.Contains("Choose rack", page);
        Assert.Contains("id=\"new-sheet-slot\"", page);
        Assert.Contains("Choose slot", page);
        Assert.Contains("data-rack-id=\"@location.ParentInventoryLocationId\"", page);
        Assert.Contains("asp-for=\"NewSheet.LocationId\" id=\"new-sheet-slot\"", page);
        Assert.DoesNotContain("Choose confirmed rack slot", page);
        Assert.DoesNotContain("@location.ParentInventoryLocation?.DisplayName — @location.LocationCode", page);
    }

    [Fact]
    public void Add_sheet_dialog_wires_both_dependent_selects_through_the_shared_script()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Index.cshtml"));

        Assert.Contains("<script src=\"~/js/dependent-select.js\" asp-append-version=\"true\"></script>", page);
        Assert.Contains("initDependentSelect(document.getElementById('new-sheet-material'), document.getElementById('new-sheet-thickness'), 'materialKey')", page);
        Assert.Contains("initDependentSelect(document.getElementById('new-sheet-rack'), document.getElementById('new-sheet-slot'), 'rackId')", page);
        Assert.DoesNotContain("option.hidden", page);
        Assert.DoesNotContain("filterThicknesses", page);
    }

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
