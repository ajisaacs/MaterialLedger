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

    [Fact]
    public void Purchase_order_lines_wire_their_dependent_selects_through_the_shared_script()
    {
        var page = ReadPage("PurchaseOrders", "Create.cshtml");

        Assert.Contains("<script src=\"~/js/dependent-select.js\" asp-append-version=\"true\"></script>", page);
        Assert.Contains("initDependentSelect(row.querySelector('.po-line-material'), row.querySelector('.po-line-thickness'), 'materialKey')", page);
        Assert.DoesNotContain("option.hidden", page);
        Assert.DoesNotContain("filterThicknesses", page);
        Assert.Contains("document.getElementById('add-line').addEventListener('click'", page);
    }

    private static string ReadPage(params string[] pathSegments) => File.ReadAllText(
        Path.Combine(new[] { RepositoryRoot, "MaterialLedger.Web", "Pages" }.Concat(pathSegments).ToArray()));

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
