using Xunit;

namespace MaterialLedger.Tests;

public sealed class OverviewPageDesignTests
{
    [Fact]
    public void Overview_presents_the_operator_workflow_and_live_operational_summary()
    {
        var page = File.ReadAllText(OverviewPagePath());

        Assert.Contains("Receive material", page);
        Assert.Contains("Find stock", page);
        Assert.Contains("Reserve material", page);
        Assert.Contains("Close out cut", page);
        Assert.Contains("asp-page=\"/Census/SelectSlot\"", page);
        Assert.Contains("asp-page=\"/Inventory/Search\"", page);
        Assert.Contains("asp-page=\"/Reservations/Index\"", page);
        Assert.Contains("asp-page=\"/Closeout/Index\"", page);
        Assert.Contains("Available material", page);
        Assert.Contains("Active reservations", page);
        Assert.Contains("Awaiting closeout", page);
        Assert.Contains("Attention needed", page);
    }

    private static string OverviewPagePath() => Path.GetFullPath(
        "../../../../MaterialLedger.Web/Pages/Index.cshtml",
        AppContext.BaseDirectory);
}
