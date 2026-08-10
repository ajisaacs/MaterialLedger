using Xunit;

namespace MaterialLedger.Tests;

public sealed class OverviewPageDesignTests
{
    [Fact]
    public void Overview_presents_actionable_work_and_live_operational_summary()
    {
        var page = File.ReadAllText(OverviewPagePath());

        Assert.Contains("Find material", page);
        Assert.Contains("Hold material for work", page);
        Assert.Contains("Record cut result", page);
        Assert.Contains("Receive delivery", page);
        Assert.Contains("Count existing material", page);
        Assert.Contains("asp-page=\"/Receiving/SelectLocation\"", page);
        Assert.Contains("asp-page=\"/Census/SelectSlot\"", page);
        Assert.Contains("asp-page=\"/Inventory/Search\"", page);
        Assert.Contains("asp-page=\"/Reservations/Index\"", page);
        Assert.Contains("asp-page=\"/Closeout/Index\"", page);
        Assert.Contains("Available material", page);
        Assert.Contains("Ready for cutting", page);
        Assert.Contains("Record cut result", page);
        Assert.Contains("Attention needed", page);
    }

    private static string OverviewPagePath() => Path.GetFullPath(
        "../../../../MaterialLedger.Web/Pages/Index.cshtml",
        AppContext.BaseDirectory);
}
