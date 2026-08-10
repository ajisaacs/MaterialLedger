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

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
