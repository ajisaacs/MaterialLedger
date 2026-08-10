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
    public void PEP_nest_details_shows_material_mapping_and_hands_off_to_a_material_hold()
    {
        var page = ReadPage("PepNests", "Details.cshtml");

        Assert.Contains("PEP material", page);
        Assert.Contains("Map to Material Ledger catalog", page);
        Assert.Contains("asp-page=\"/Reservations/Create\"", page);
        Assert.Contains("asp-route-pepNestYear", page);
        Assert.Contains("asp-route-pepNestName", page);
        Assert.Contains("asp-route-pepMaterialNumber", page);
        Assert.Contains("Create material hold", page);
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
