using Xunit;

namespace MaterialLedger.Tests;

public class InventorySearchPresentationTests
{
    [Fact]
    public void SearchPage_UsesCompactFilterBarWithExpandableAdvancedFilters()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var markup = File.ReadAllText(Path.Combine(repositoryRoot, "MaterialLedger.Web", "Pages", "Inventory", "Search.cshtml"));

        Assert.Contains("class=\"inventory-search-bar\"", markup);
        Assert.Contains("class=\"dimension-filter\"", markup);
        Assert.Contains("<details class=\"advanced-filters\"", markup);
        Assert.Contains("<summary>More filters</summary>", markup);
        Assert.Contains("class=\"active-filter-summary\"", markup);
        Assert.Contains("Clear all", markup);
    }
}
