using Xunit;

namespace MaterialLedger.Tests;

public sealed class NavigationPresentationTests
{
    [Fact]
    public void Navigation_maps_each_visible_action_to_its_exact_page_route()
    {
        var layout = ReadPage("Shared", "_Layout.cshtml");

        Assert.Contains("<a asp-page=\"/Index\">Control board</a>", layout);
        Assert.Contains("<a asp-page=\"/Inventory/Search\">Find material</a>", layout);
        Assert.Contains("<a asp-page=\"/Reservations/Index\">Material holds</a>", layout);
        Assert.Contains("<a asp-page=\"/Closeout/Index\">Record cut result</a>", layout);
        Assert.Contains("<a asp-page=\"/Receiving/SelectLocation\">Receive delivery</a>", layout);
        Assert.Contains("<a asp-page=\"/Census/SelectSlot\">Count existing material</a>", layout);
        Assert.Contains("<a asp-page=\"/Locations/Index\">Material locations</a>", layout);
        Assert.Contains("<a asp-page=\"/PurchaseOrders/Index\">Order material</a>", layout);
        Assert.Contains("<a asp-page=\"/Exceptions/Index\">Attention needed</a>", layout);
        Assert.DoesNotContain("disabled", layout);
    }

    [Fact]
    public void Navigation_renders_privileged_actions_only_inside_their_authorized_role_gates()
    {
        var layout = ReadPage("Shared", "_Layout.cshtml");

        var operatorManagerGate = RoleGateBlock(
            layout,
            "@if (User.IsInRole(\"Operator\") || User.IsInRole(\"Manager\"))");
        var programmerManagerGate = RoleGateBlock(
            layout,
            "@if (User.IsInRole(\"Programmer\") || User.IsInRole(\"Manager\"))");
        const string closeoutLink = "<a asp-page=\"/Closeout/Index\">Record cut result</a>";
        const string receivingLink = "<a asp-page=\"/Receiving/SelectLocation\">Receive delivery</a>";
        const string orderMaterialLink = "<a asp-page=\"/PurchaseOrders/Index\">Order material</a>";
        const string censusLink = "<a asp-page=\"/Census/SelectSlot\">Count existing material</a>";

        Assert.Equal(1, CountOccurrences(layout, closeoutLink));
        Assert.Equal(1, CountOccurrences(layout, receivingLink));
        Assert.Equal(1, CountOccurrences(layout, orderMaterialLink));
        Assert.Equal(1, CountOccurrences(layout, censusLink));
        Assert.Contains(closeoutLink, operatorManagerGate);
        Assert.Contains(receivingLink, programmerManagerGate);
        Assert.Contains(orderMaterialLink, programmerManagerGate);
        Assert.Contains(censusLink, programmerManagerGate);
    }

    [Fact]
    public void Supporting_pages_use_shop_readable_labels_and_semantic_feedback()
    {
        var search = ReadPage("Inventory", "Search.cshtml");
        var locations = ReadPage("Locations", "Index.cshtml");
        var locationDetails = ReadPage("Locations", "Details.cshtml");
        var attention = ReadPage("Exceptions", "Index.cshtml");

        Assert.Contains("Hold for work", search);
        Assert.Contains("role=\"status\"", search);
        Assert.Contains("Material locations", locations);
        Assert.Contains("Material locations", locationDetails);
        Assert.Contains("inventory-status", locationDetails);
        Assert.DoesNotContain("style=\"@statusStyle\"", locationDetails);
        Assert.Contains("What needs attention", attention);
        Assert.Contains("role=\"alert\"", attention);
        Assert.Contains("AwaitingCloseoutOverdue", attention);
        Assert.Contains("Cut result has not been recorded", attention);
    }

    [Fact]
    public void Fixed_sidebar_reserves_its_width_from_the_workspace_without_page_level_horizontal_overflow()
    {
        var css = CssTestHelper.ReadEffectiveAppCss();

        Assert.Matches("\\.workspace\\s*\\{[\\s\\S]*?margin-left: 246px;[\\s\\S]*?width: calc\\(100% - 246px\\);", css);
        Assert.Matches("@media \\(max-width: 1050px\\)[\\s\\S]*?\\.workspace\\s*\\{[\\s\\S]*?margin-left: 215px;[\\s\\S]*?width: calc\\(100% - 215px\\);", css);
    }

    [Fact]
    public void Shared_styles_prioritize_feedback_keyboard_focus_and_tables_with_a_persistent_sidebar()
    {
        var css = CssTestHelper.ReadEffectiveAppCss();

        Assert.Contains(":focus-visible", css);
        Assert.Contains("--focus-ring: #005a9c", css);
        Assert.Contains("--rail-focus-ring: #7ee7f5", css);
        Assert.Contains("outline: 3px solid var(--focus-ring)", css);
        Assert.Contains(".sidebar :focus-visible", css);
        Assert.Contains(".sidebar {", css);
        Assert.Contains("height: 100vh", css);
        Assert.Contains("position: fixed", css);
        Assert.Contains(".primary-nav {", css);
        Assert.Contains("overflow-y: auto", css);
        Assert.Contains(".account {", css);
        Assert.Contains("flex-shrink: 0", css);
        Assert.Contains("outline: 3px solid var(--rail-focus-ring)", css);
        Assert.Contains(".alert-success", css);
        Assert.Contains(".alert-error", css);
        Assert.Contains(".inventory-status", css);
        Assert.Matches("@media \\(max-width: 1050px\\)[\\s\\S]*?table \\{[\\s\\S]*?overflow-x: auto", css);
    }

    [Fact]
    public void Shared_form_buttons_align_with_the_bottom_of_neighboring_stacked_fields()
    {
        var css = CssTestHelper.ReadEffectiveAppCss();

        Assert.Matches("button\\s*\\{[\\s\\S]*?vertical-align: bottom;", css);
    }

    private static string RoleGateBlock(string source, string gateHeader)
    {
        var gateStart = source.IndexOf(gateHeader, StringComparison.Ordinal);
        Assert.True(gateStart >= 0, $"Missing role gate: {gateHeader}");

        var blockStart = source.IndexOf('{', gateStart + gateHeader.Length);
        Assert.True(blockStart >= 0, $"Missing opening brace for role gate: {gateHeader}");

        var blockEnd = source.IndexOf('}', blockStart + 1);
        Assert.True(blockEnd >= 0, $"Missing closing brace for role gate: {gateHeader}");

        return source[blockStart..(blockEnd + 1)];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = source.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
    }

    private static string ReadPage(params string[] pathSegments) => File.ReadAllText(
        Path.Combine(new[] { RepositoryRoot, "MaterialLedger.Web", "Pages" }.Concat(pathSegments).ToArray()));

    private static string RepositoryRoot => Path.GetFullPath(
        "../../../../",
        AppContext.BaseDirectory);
}
