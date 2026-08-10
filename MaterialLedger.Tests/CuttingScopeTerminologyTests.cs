using Xunit;

namespace MaterialLedger.Tests;

public sealed class CuttingScopeTerminologyTests
{
    [Fact]
    public void Operator_facing_workflow_copy_is_cutting_wide_not_laser_specific()
    {
        var layout = ReadPage("Shared", "_Layout.cshtml");
        var hold = ReadPage("Reservations", "Create.cshtml");
        var search = ReadPage("Inventory", "Search.cshtml");
        var closeout = ReadPage("Closeout", "Index.cshtml");
        var details = ReadPage("Reservations", "Details.cshtml");
        var controlBoard = ReadPage("Index.cshtml");

        Assert.Contains("<p class=\"rail-caption\">Material control</p>", layout);
        Assert.Contains("<p>Cutting workflow</p>", layout);
        Assert.Contains("<p class=\"eyebrow\">Cutting workflow</p>", hold);
        Assert.Contains("<p class=\"eyebrow\">Cutting workflow</p>", search);
        Assert.Contains("<p class=\"eyebrow\">Cutting work queue</p>", closeout);
        Assert.Contains("Cutting operator", details);
        Assert.Contains("Program record ID", hold);
        Assert.Contains("Program record ID", details);
        Assert.Contains("approved holds", controlBoard);
        Assert.Contains("work item", controlBoard);

        Assert.DoesNotContain("Laser material control", layout);
        Assert.DoesNotContain(">Work now<", layout);
        Assert.DoesNotContain(">Work now<", hold);
        Assert.DoesNotContain(">Work now<", search);
        Assert.DoesNotContain("Laser work queue", closeout);
        Assert.DoesNotContain("Laser operator", details);
        Assert.DoesNotContain("Laser program record ID", hold);
        Assert.DoesNotContain("Laser program record ID", details);
        Assert.DoesNotContain("laser work", controlBoard, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("approved laser holds", controlBoard, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Control_board_counts_and_queue_links_cover_all_cutting_equipment()
    {
        var query = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Services", "WorkQueueQueryService.cs"));
        var controlBoard = ReadPage("Index.cshtml");

        Assert.DoesNotContain("ReservationEquipment.Laser", query);
        Assert.Contains("row.Reservation.Status == ReservationStatus.Approved", query);
        Assert.Contains("row.Reservation.Status == ReservationStatus.AwaitingCloseout", query);
        Assert.DoesNotContain("asp-route-Equipment=\"Laser\"", controlBoard);
    }

    private static string ReadPage(params string[] pathSegments) => File.ReadAllText(
        Path.Combine(new[] { RepositoryRoot, "MaterialLedger.Web", "Pages" }.Concat(pathSegments).ToArray()));

    private static string RepositoryRoot => Path.GetFullPath(
        "../../../../",
        AppContext.BaseDirectory);
}
