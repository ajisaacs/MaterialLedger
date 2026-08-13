using Xunit;

namespace MaterialLedger.Tests;

public class InventorySearchPresentationTests
{
    [Fact]
    public void SearchPage_UsesSelectableColumnsAndOptionModals()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var markup = File.ReadAllText(Path.Combine(repositoryRoot, "MaterialLedger.Web", "Pages", "Inventory", "Search.cshtml"));
        var partial = File.ReadAllText(Path.Combine(repositoryRoot, "MaterialLedger.Web", "Pages", "Inventory", "_InventoryOptionFilter.cshtml"));
        var css = File.ReadAllText(Path.Combine(repositoryRoot, "MaterialLedger.Web", "wwwroot", "css", "inventory.css"));

        Assert.Contains("class=\"inventory-filter-columns\"", markup);
        Assert.Contains("class=\"inventory-filter-toolbar\"", markup);
        Assert.Contains("Filter:", markup);
        Assert.Contains("data-filter-dialog=\"thickness-filter-dialog\"", markup);
        Assert.Contains("data-filter-dialog=\"size-filter-dialog\"", markup);
        Assert.Contains("id=\"size-filter-dialog\"", markup);
        Assert.Contains("size-filter-fields", markup);
        Assert.Contains("size-filter-grid", markup);
        Assert.Contains("size-filter-separator", markup);
        Assert.Contains("grid-template-columns: minmax(0, 1fr) 1.5rem minmax(0, 1fr)", css);
        Assert.Contains("height: 2.75rem", css);
        Assert.DoesNotContain("Apply filters", markup);
        Assert.Contains("Select all", partial);
        Assert.Contains("Deselect all", partial);
        Assert.Contains("data-selection-count", partial);
        Assert.Contains("data-apply-filter", partial);
        Assert.Contains("requestSubmit()", markup);
        Assert.Contains("name=\"@Model.ValueFieldName\"", partial);
        Assert.Contains("inventory-option-dialog", css);
        Assert.Contains("inventory-filter-column", css);
        Assert.DoesNotContain("inventory-filter-dialog", markup);
    }
}
