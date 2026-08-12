using Xunit;

namespace MaterialLedger.Tests;

public class InventorySearchPresentationTests
{
    [Fact]
    public void SearchPage_ShowsAppliedFiltersAndMovesTheFullFormIntoAModal()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var markup = File.ReadAllText(Path.Combine(repositoryRoot, "MaterialLedger.Web", "Pages", "Inventory", "Search.cshtml"));

        Assert.Contains("class=\"active-filter-list\"", markup);
        Assert.Contains("<strong>Availability:</strong>", markup);
        Assert.Contains("id=\"inventory-filter-dialog\"", markup);
        Assert.Contains("class=\"inventory-filter-dialog\"", markup);
        Assert.Contains("Filter material", markup);
        Assert.Contains("Apply filters", markup);
        Assert.Contains("Clear all filters", markup);
        Assert.DoesNotContain("advanced-filters", markup);
    }
}
