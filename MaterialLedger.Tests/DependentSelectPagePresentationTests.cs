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
    }

    [Fact]
    public void Purchase_order_page_uses_vendor_reference_ids_and_creates_inline_references_before_submit()
    {
        var page = ReadPage("PurchaseOrders", "Create.cshtml");
        var model = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "PurchaseOrders", "Create.cshtml.cs"));
        Assert.Contains("asp-for=\"VendorId\"", page);
        Assert.Contains("asp-for=\"Lines[i].VendorPartId\"", page);
        Assert.Contains("New vendor name", page);
        Assert.Contains("New vendor part number", page);
        Assert.Contains("FindOrCreateActiveVendorAsync", model);
        Assert.Contains("FindOrCreateActivePartAsync", model);
        Assert.DoesNotContain("public string? VendorName { get", model);
        Assert.DoesNotContain("public string? VendorPartNumber { get", model);
    }

    [Fact]
    public void Receiving_page_uses_reference_id_pickers_and_locks_po_identity()
    {
        var page = ReadPage("Receiving", "NewReceipt.cshtml");
        var model = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Receiving", "NewReceipt.cshtml.cs"));
        Assert.Contains("asp-for=\"VendorId\"", page);
        Assert.Contains("asp-for=\"VendorPartId\"", page);
        Assert.Contains("Reference values are locked to the PO", page);
        Assert.DoesNotContain("public string? VendorName { get", model);
        Assert.DoesNotContain("public string? VendorPartNumber { get", model);
    }

    private static string ReadPage(params string[] pathSegments) => File.ReadAllText(Path.Combine(new[] { RepositoryRoot, "MaterialLedger.Web", "Pages" }.Concat(pathSegments).ToArray()));
    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
