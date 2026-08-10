using MaterialLedger.Data.Models;
using MaterialLedger.Web.Shared;
using Xunit;

namespace MaterialLedger.Tests;

public sealed class ShopFloorWorkflowPresentationTests
{
    [Fact]
    public void Control_board_exposes_physical_work_actions()
    {
        var page = ReadPage("Index.cshtml");

        Assert.Contains("Find material", page);
        Assert.Contains("Hold material for work", page);
        Assert.Contains("Record cut result", page);
    }

    [Fact]
    public void Receiving_and_opening_count_have_separate_entry_routes()
    {
        var overview = ReadPage("Index.cshtml");
        var layout = ReadPage("Shared", "_Layout.cshtml");

        Assert.Contains("asp-page=\"/Receiving/SelectLocation\"", overview);
        Assert.Contains("Receive delivery", overview);
        Assert.Contains("asp-page=\"/Census/SelectSlot\"", overview);
        Assert.Contains("Count existing material", overview);
        Assert.Contains("asp-page=\"/Receiving/SelectLocation\"", layout);
    }

    [Fact]
    public void Closeout_uses_one_outcome_selection_form()
    {
        var page = ReadPage("Closeout", "Details.cshtml");

        Assert.Contains("After the cut, what remains?", page);
        Assert.Equal(1, CountOccurrences(page, "<form"));
        Assert.Contains("name=\"Outcome\"", page);
    }

    [Fact]
    public void Control_board_action_links_target_the_same_filtered_queues_as_their_counts()
    {
        var page = ReadPage("Index.cshtml");

        Assert.Contains("asp-page=\"/Reservations/Index\" asp-route-Status=\"Approved\"", page);
        Assert.Contains("asp-page=\"/Closeout/Index\" asp-route-Status=\"AwaitingCloseout\"", page);
        Assert.DoesNotContain("asp-route-Equipment=\"Laser\"", page);
        Assert.Contains("asp-page=\"/Reservations/Index\" asp-route-Status=\"Proposed\"", page);
    }

    [Fact]
    public void Queue_handlers_accept_status_and_equipment_filters()
    {
        var reservations = ReadPage("Reservations", "Index.cshtml.cs");
        var closeout = ReadPage("Closeout", "Index.cshtml.cs");

        Assert.Contains("[BindProperty(SupportsGet = true)] public ReservationStatus? Status", reservations);
        Assert.Contains("[BindProperty(SupportsGet = true)] public ReservationEquipment? Equipment", reservations);
        Assert.Contains("[BindProperty(SupportsGet = true)] public ReservationStatus? Status", closeout);
        Assert.Contains("[BindProperty(SupportsGet = true)] public ReservationEquipment? Equipment", closeout);
    }

    [Theory]
    [InlineData(ReservationStatus.Proposed, "Held — approval needed")]
    [InlineData(ReservationStatus.Approved, "Ready for cutting")]
    [InlineData(ReservationStatus.AwaitingCloseout, "Cut finished — record result")]
    public void Work_queues_use_the_shared_plain_language_workflow_display(
        ReservationStatus status,
        string expectedLabel)
    {
        var reservations = ReadPage("Reservations", "Index.cshtml");
        var closeout = ReadPage("Closeout", "Index.cshtml");

        Assert.Matches(@"ReservationWorkflowDisplay\.For\([^)]*\)", reservations);
        Assert.Contains(".Label", reservations);
        Assert.Matches(@"ReservationWorkflowDisplay\.For\([^)]*\)", closeout);
        Assert.Contains(".Label", closeout);

        var display = ReservationWorkflowDisplay.For(status);
        Assert.Equal(expectedLabel, display.Label);
        Assert.DoesNotContain(status.ToString(), display.Label, StringComparison.Ordinal);
    }

    private static string ReadPage(params string[] pathSegments) => File.ReadAllText(
        Path.Combine(new[] { RepositoryRoot, "MaterialLedger.Web", "Pages" }.Concat(pathSegments).ToArray()));

    private static string RepositoryRoot => Path.GetFullPath(
        "../../../../",
        AppContext.BaseDirectory);

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(fragment, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += fragment.Length;
        }

        return count;
    }
}
