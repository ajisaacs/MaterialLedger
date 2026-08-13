using Xunit;

namespace MaterialLedger.Tests;

public sealed class PepNestPresentationTests
{
    [Fact]
    public void PEP_nest_browser_exposes_filters_and_a_details_route()
    {
        var page = ReadPage("PepNests", "Index.cshtml");

        Assert.Contains("View PEP nests", page);
        Assert.Contains("asp-for=\"Year\"", page);
        Assert.Contains("asp-for=\"Status\"", page);
        Assert.Contains("asp-page=\"Details\"", page);
        Assert.Contains("asp-route-year", page);
        Assert.Contains("asp-route-nestName", page);
    }

    [Fact]
    public void PEP_nest_browser_shows_newest_first_and_a_descriptive_material_with_grade()
    {
        var page = ReadPage("PepNests", "Index.cshtml");
        var model = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "PepNests", "Index.cshtml.cs"));

        Assert.Contains("OrderByDescending(x => x.DateCreated)", model);
        Assert.Contains("@nest.MaterialDescription", page);
        Assert.Contains("@nest.MaterialGrade", page);
        Assert.DoesNotContain("#@nest.MaterialNumber", page);
    }

    [Fact]
    public void PEP_nest_browser_status_filter_is_a_dropdown_defaulting_to_To_be_cut()
    {
        var page = ReadPage("PepNests", "Index.cshtml");
        var model = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "PepNests", "Index.cshtml.cs"));

        Assert.Contains("<select asp-for=\"Status\">", page);
        Assert.DoesNotContain("list=\"statusOptions\"", page);
        Assert.Contains("All statuses", page);
        Assert.Contains("IndexModel.StatusOptions", page);
        Assert.Contains("@foreach (var status in IndexModel.StatusOptions)", page);

        Assert.Contains("Status { get; set; } = \"To be cut\";", model);
        Assert.Contains("\"To be cut\", \"Quote\", \"Has been cut\", \"Hold\",", model);
        Assert.Contains("\"Quote, accepted, to be cut\", \"Quote, accepted, has been cut\",", model);
        Assert.Contains("\"Quote, accepted, hold\", \"Quote, accepted, moved to wip\",", model);
        Assert.Contains("\"Quote, rejected\", \"Quote, timed out\", \"Deleted\", \"N/A\",", model);
    }

    [Fact]
    public void PEP_nest_details_shows_material_mapping_in_the_PEP_material_card_and_can_reserve_the_complete_program_sheet_set()
    {
        var page = ReadPage("PepNests", "Details.cshtml");
        var model = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "PepNests", "Details.cshtml.cs"));

        Assert.Contains("PEP material", page);
        Assert.Contains("pep-nest-mapping-summary", page);
        Assert.Contains("pep-material-mapping-dialog", page);
        Assert.Contains("Map catalog material", page);
        Assert.Contains("Change mapping", page);
        Assert.Contains("showModal()", page);
        Assert.Contains("Catalog material", page);
        Assert.Contains("Thickness", page);
        Assert.Contains("pep-material-group", page);
        Assert.Contains("pep-material-thickness", page);
        Assert.Contains("data-material-group", page);
        Assert.Contains("updateThicknesses", page);
        Assert.Contains("Program sheet size", page);
        Assert.Contains("ProgramSheetRequirements", page);
        Assert.Contains("asp-page-handler=\"ReserveProgramSheets\"", page);
        Assert.Contains("Reserve all program sheets", page);
        Assert.Contains("creates all required holds together or creates none", page);
        Assert.Contains("pep-nest-requirements-action", page);
        Assert.True(page.IndexOf("asp-page-handler=\"ReserveProgramSheets\"", StringComparison.Ordinal) < page.IndexOf("pep-nest-stock-panel", StringComparison.Ordinal));
        Assert.Contains("CreateReservationBatchAsync", model);
        Assert.Contains("No holds were created", model);
        Assert.Contains("BuildReservationPlan", model);
        Assert.Contains("OnPostRecordFoundSheetAsync", model);
        Assert.Contains("RecordOpeningBalanceAsync", model);
        Assert.Contains("ProgramSheetShortfall", model);
        Assert.Contains("pep-found-sheet-dialog", page);
        Assert.Contains("Record found sheet and retry reservation", page);
        Assert.Contains("Continue inventory search", page);
        Assert.Contains("FoundSheetLocationId", page);
        Assert.Contains("ShowFoundSheetDialog", page);
        Assert.True(page.IndexOf("pep-nest-mapping-summary", StringComparison.Ordinal) < page.IndexOf("pep-nest-requirements-card", StringComparison.Ordinal));
    }

    [Fact]
    public void PEP_nest_details_uses_a_dedicated_nest_workspace_layout()
    {
        var page = ReadPage("PepNests", "Details.cshtml");
        var css = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "css", "pep-nests.css"));
        var appCss = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "css", "app.css"));

        Assert.Contains("pep-nest-workspace", page);
        Assert.Contains("pep-nest-overview", page);
        Assert.Contains("pep-nest-parts-panel", page);
        Assert.Contains("pep-nest-material-card", page);
        Assert.Contains(".pep-nest-mapping-summary", css);
        Assert.Contains(".pep-material-mapping-dialog", css);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr));", css);
        Assert.Contains(".pep-nest-workspace", css);
        Assert.Contains(".pep-nest-overview", css);
        Assert.Contains("@import url(\"pep-nests.css\");", appCss);
    }

    [Fact]
    public void PEP_nest_details_includes_a_part_size_column_that_is_safe_when_PEP_omits_it()
    {
        var page = ReadPage("PepNests", "Details.cshtml");
        var contract = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Services", "PepNestQueryService.cs"));

        Assert.Contains("<th>Part size</th>", page);
        Assert.Contains("part.PartSize ?? part.Size ?? \"—\"", page);
        Assert.Contains("public string? PartSize", contract);
        Assert.Contains("public string? Size", contract);
    }

    [Fact]
    public void PEP_nest_details_shows_a_warning_when_the_program_has_been_cut()
    {
        var page = ReadPage("PepNests", "Details.cshtml");

        Assert.Contains("warning-banner", page);
        Assert.Contains("Contains(\"has been cut\", StringComparison.OrdinalIgnoreCase)", page);
        Assert.Contains("already been cut", page);
    }

    [Fact]
    public void Navigation_links_authorized_users_to_PEP_nests()
    {
        var layout = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Shared", "_Layout.cshtml"));

        Assert.Contains("asp-page=\"/PepNests/Index\"", layout);
        Assert.Contains(">PEP nests<", layout);
    }

    private static string ReadPage(params string[] pathSegments) => File.ReadAllText(
        Path.Combine(new[] { RepositoryRoot, "MaterialLedger.Web", "Pages" }.Concat(pathSegments).ToArray()));

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
