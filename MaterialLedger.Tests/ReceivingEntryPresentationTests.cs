using Xunit;

namespace MaterialLedger.Tests;

public sealed class ReceivingEntryPresentationTests
{
    [Fact]
    public void Control_board_separates_delivery_receipt_from_existing_material_count()
    {
        var page = ReadPage("Index.cshtml");

        Assert.Contains("Receive delivery", page);
        Assert.Contains("asp-page=\"/Receiving/SelectLocation\"", page);
        Assert.Contains("Count existing material", page);
        Assert.Contains("asp-page=\"/Census/SelectSlot\"", page);
    }

    [Fact]
    public void Navigation_offers_delivery_receipt_and_existing_material_count_as_distinct_actions()
    {
        var layout = ReadPage("Shared", "_Layout.cshtml");

        Assert.Contains("Receive delivery", layout);
        Assert.Contains("asp-page=\"/Receiving/SelectLocation\"", layout);
        Assert.Contains("Count existing material", layout);
        Assert.Contains("asp-page=\"/Census/SelectSlot\"", layout);
    }

    [Fact]
    public void Receiving_location_picker_is_limited_to_delivery_receipt()
    {
        var page = ReadPage("Receiving", "SelectLocation.cshtml");

        Assert.Contains("Receive delivery", page);
        Assert.Contains("Receive delivery here", page);
        Assert.DoesNotContain("Record opening balance", page);
        Assert.DoesNotContain("Count existing material", page);
    }

    [Fact]
    public void Existing_material_count_picker_is_limited_to_counted_balance()
    {
        var page = ReadPage("Census", "SelectSlot.cshtml");

        Assert.Contains("Count existing material", page);
        Assert.Contains("Record counted balance", page);
        Assert.DoesNotContain("Receive material", page);
        Assert.DoesNotContain("Receive delivery", page);
    }

    [Fact]
    public void Receipt_success_continues_to_delivery_receipt_not_existing_material_count()
    {
        var page = ReadPage("Receiving", "Success.cshtml");

        Assert.Contains("Receive another delivery", page);
        Assert.Contains("asp-page=\"/Receiving/SelectLocation\"", page);
        Assert.DoesNotContain("asp-page=\"/Census/SelectSlot\"", page);
    }

    private static string ReadPage(params string[] pathSegments) => File.ReadAllText(
        Path.Combine(new[] { RepositoryRoot, "MaterialLedger.Web", "Pages" }.Concat(pathSegments).ToArray()));

    private static string RepositoryRoot => Path.GetFullPath(
        "../../../../",
        AppContext.BaseDirectory);
}
