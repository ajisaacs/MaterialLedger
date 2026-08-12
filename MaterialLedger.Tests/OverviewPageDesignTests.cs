using Xunit;

namespace MaterialLedger.Tests;

public sealed class OverviewPageDesignTests
{
    [Fact]
    public void Overview_is_a_compact_operational_workboard()
    {
        var page = File.ReadAllText(OverviewPagePath());

        Assert.Contains("<h1>Workboard</h1>", page);
        Assert.DoesNotContain("What needs to happen now?", page);
        Assert.DoesNotContain("class=\"summary-grid\"", page);
        Assert.DoesNotContain("class=\"workflow-grid\"", page);
        Assert.Contains("Action queue", page);
        Assert.Contains("action-queue-row", page);
        Assert.Contains("No material work needs action.", page);
        Assert.Contains("<form class=\"workboard-search\" method=\"get\" action=\"/Inventory/Search\">", page);
        Assert.Contains("<label for=\"workboard-material-family\">Material, grade, finish, or location</label>", page);
        Assert.Contains("name=\"MaterialFamily\"", page);
        Assert.Contains("name=\"AvailableOnly\" value=\"true\"", page);
        Assert.Contains("Advanced filters", page);
        Assert.Contains("Quick actions", page);
        Assert.Contains("Recent activity", page);
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
        Assert.Contains("Ready for cutting", page);
        Assert.Contains("Attention needed", page);
    }

    private static string OverviewPagePath() => Path.GetFullPath(
        "../../../../MaterialLedger.Web/Pages/Index.cshtml",
        AppContext.BaseDirectory);
}
