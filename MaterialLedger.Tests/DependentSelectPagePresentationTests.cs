using Xunit;

namespace MaterialLedger.Tests;

public sealed class DependentSelectPagePresentationTests
{
    [Fact]
    public void Opening_balance_page_wires_its_dependent_select_through_the_shared_script()
    {
        var page = ReadPage("Census", "NewOpeningBalance.cshtml");

        Assert.Contains("<script src=\"~/js/dependent-select.js\" asp-append-version=\"true\"></script>", page);
        Assert.Contains("initDependentSelect(document.getElementById('opening-balance-material'), document.getElementById('opening-balance-thickness'), 'materialKey')", page);
        Assert.DoesNotContain("option.hidden", page);
        Assert.DoesNotContain("filterThicknesses", page);
    }

    private static string ReadPage(params string[] pathSegments) => File.ReadAllText(
        Path.Combine(new[] { RepositoryRoot, "MaterialLedger.Web", "Pages" }.Concat(pathSegments).ToArray()));

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
