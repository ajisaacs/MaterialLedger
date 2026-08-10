using Xunit;

namespace MaterialLedger.Tests;

public sealed class ReservationQueuePresentationTests
{
    [Fact]
    public void Reservation_queue_separates_work_by_physical_next_step()
    {
        var page = ReadPage("Reservations", "Index.cshtml");
        var model = ReadPage("Reservations", "Index.cshtml.cs");

        Assert.Contains("Approval needed", model);
        Assert.Contains("Ready for cutting", model);
        Assert.Contains("Cut result needed", model);
        Assert.Contains("Workflow status", page);
        Assert.DoesNotContain("<th>Status</th>", page);
    }

    [Fact]
    public void Reservation_queue_shows_operator_context_and_role_appropriate_actions()
    {
        var page = ReadPage("Reservations", "Index.cshtml");

        Assert.Contains("Work reference", page);
        Assert.Contains("Usable size", page);
        Assert.Contains("Location", page);
        Assert.Contains("Review approval", page);
        Assert.Contains("Record cut result", page);
        Assert.Matches(
            @"queueSection\.Kind\s*==\s*ReservationQueueKind\.ApprovalNeeded\s*&&\s*\(\s*User\.IsInRole\(\""Programmer\""\)\s*\|\|\s*User\.IsInRole\(\""Manager\""\)\s*\)",
            page);
        Assert.Contains("User.IsInRole(\"Manager\")", page);
        Assert.Contains("User.IsInRole(\"Programmer\")", page);
        Assert.Contains("User.IsInRole(\"Operator\")", page);
    }

    [Fact]
    public void Reservation_queue_preserves_status_and_equipment_query_filters()
    {
        var model = ReadPage("Reservations", "Index.cshtml.cs");

        Assert.Contains("[BindProperty(SupportsGet = true)] public ReservationStatus? Status", model);
        Assert.Contains("[BindProperty(SupportsGet = true)] public ReservationEquipment? Equipment", model);
        Assert.Contains("if (Status is not null)", model);
        Assert.Contains("if (Equipment is not null)", model);
    }

    [Fact]
    public void Reservation_queue_uses_shared_workflow_display_mapping()
    {
        var page = ReadPage("Reservations", "Index.cshtml");

        Assert.Contains("ReservationWorkflowDisplay.For(row.Reservation.Status)", page);
    }

    private static string ReadPage(params string[] pathSegments) => File.ReadAllText(
        Path.Combine(new[] { RepositoryRoot, "MaterialLedger.Web", "Pages" }.Concat(pathSegments).ToArray()));

    private static string RepositoryRoot => Path.GetFullPath(
        "../../../../",
        AppContext.BaseDirectory);
}
