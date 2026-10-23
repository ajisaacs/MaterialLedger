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

        Assert.Equal(2, CountOccurrences(layout, closeoutLink));
        Assert.Equal(2, CountOccurrences(layout, receivingLink));
        Assert.Equal(2, CountOccurrences(layout, orderMaterialLink));
        Assert.Equal(2, CountOccurrences(layout, censusLink));
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
        Assert.Matches("\\.brand\\s*\\{[\\s\\S]*?border-bottom: 1px solid var\\(--rail-line\\)", css);
        Assert.Matches("\\.primary-nav p:first-child\\s*\\{[\\s\\S]*?border-bottom: 1px solid var\\(--rail-line\\)", css);
        Assert.Contains(".account {", css);
        Assert.Contains("flex-shrink: 0", css);
        Assert.Contains("outline: 3px solid var(--rail-focus-ring)", css);
        Assert.Contains(".alert-success", css);
        Assert.Contains(".alert-error", css);
        Assert.Contains(".inventory-status", css);
        Assert.Matches("@media \\(max-width: 1050px\\)[\\s\\S]*?table \\{[\\s\\S]*?overflow-x: auto", css);
    }

    [Fact]
    public void Authenticated_layout_references_an_existing_favicon_to_avoid_browser_asset_errors()
    {
        var layout = ReadPage("Shared", "_Layout.cshtml");
        var faviconPath = Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "favicon.svg");

        Assert.Contains("href=\"~/favicon.svg\"", layout);
        Assert.True(File.Exists(faviconPath), $"Missing favicon asset: {faviconPath}");
    }

    [Fact]
    public void Mobile_navigation_uses_a_compact_native_menu_that_preserves_grouped_links()
    {
        var layout = ReadPage("Shared", "_Layout.cshtml");
        var css = CssTestHelper.ReadEffectiveAppCss();

        Assert.Contains("<nav class=\"primary-nav\" aria-label=\"Primary navigation\">", layout);
        Assert.Contains("<details class=\"mobile-primary-nav-container\">", layout);
        Assert.Contains("<nav class=\"primary-nav mobile-primary-nav\" aria-label=\"Primary navigation\">", layout);
        Assert.Matches("\\.mobile-primary-nav-container\\s*\\{[\\s\\S]*?display: none", css);
        Assert.Matches("@media \\(max-width: 720px\\)[\\s\\S]*?\\.sidebar > \\.primary-nav\\s*\\{[\\s\\S]*?display: none", css);
        Assert.Matches("@media \\(max-width: 720px\\)[\\s\\S]*?\\.mobile-primary-nav-container\\s*\\{[\\s\\S]*?display: block", css);
        Assert.Matches("@media \\(max-width: 720px\\)[\\s\\S]*?\\.mobile-primary-nav-container > summary\\s*\\{[\\s\\S]*?display: flex", css);
        Assert.Matches("@media \\(max-width: 720px\\)[\\s\\S]*?\\.mobile-primary-nav\\s*\\{[\\s\\S]*?max-height: min\\(60vh, 28rem\\);[\\s\\S]*?overflow-y: auto", css);
    }

    [Fact]
    public void Queue_pages_render_mobile_card_equivalents_without_changing_desktop_tables()
    {
        var search = ReadPage("Inventory", "Search.cshtml");
        var nests = ReadPage("Nests", "Index.cshtml");
        var reservations = ReadPage("Reservations", "Index.cshtml");
        var closeout = ReadPage("Closeout", "Index.cshtml");
        var css = CssTestHelper.ReadEffectiveAppCss();

        Assert.Contains("class=\"desktop-table\"", search);
        Assert.Contains("mobile-card-grid mobile-card", search);
        Assert.Contains("class=\"inventory-card\"", search);
        Assert.Contains("class=\"desktop-table\"", nests);
        Assert.Contains("mobile-card-grid mobile-card", nests);
        Assert.Contains("class=\"pep-nest-card\"", nests);
        Assert.Contains("class=\"desktop-table\"", reservations);
        Assert.Contains("mobile-card-grid mobile-card", reservations);
        Assert.Contains("class=\"workflow-queue-card\"", reservations);
        Assert.Contains("class=\"desktop-table\"", closeout);
        Assert.Contains("mobile-card-grid mobile-card", closeout);
        Assert.Contains("class=\"closeout-card\"", closeout);
        Assert.Matches("\\.mobile-card\\s*\\{[\\s\\S]*?display: none", css);
        Assert.Matches("@media \\(max-width: 720px\\)[\\s\\S]*?\\.desktop-table\\s*\\{[\\s\\S]*?display: none", css);
        Assert.Matches("@media \\(max-width: 720px\\)[\\s\\S]*?\\.mobile-card\\s*\\{[\\s\\S]*?display: block", css);
    }

    [Fact]
    public void Control_board_recent_work_uses_mobile_cards_without_changing_desktop_table()
    {
        var controlBoard = ReadPage("Index.cshtml");
        var css = CssTestHelper.ReadEffectiveAppCss();

        Assert.Contains("class=\"recent-work-table desktop-table\"", controlBoard);
        Assert.Contains("class=\"recent-work-card-grid mobile-card\"", controlBoard);
        Assert.Contains("class=\"recent-work-card\"", controlBoard);
        Assert.Contains("Material item", controlBoard);
        Assert.Contains("Workflow", controlBoard);
        Assert.Matches("\\.recent-work-card\\s*\\{[\\s\\S]*?border: 1px solid var\\(--line\\)", css);
        Assert.Matches("@media \\(max-width: 720px\\)[\\s\\S]*?\\.desktop-table\\s*\\{[\\s\\S]*?display: none", css);
        Assert.Matches("@media \\(max-width: 720px\\)[\\s\\S]*?\\.mobile-card\\s*\\{[\\s\\S]*?display: block", css);
    }

    [Fact]
    public void Workboard_uses_compact_queue_rows_and_remains_usable_on_mobile()
    {
        var controlBoard = ReadPage("Index.cshtml");
        var css = CssTestHelper.ReadEffectiveAppCss();

        Assert.Contains("class=\"workboard-toolbar\"", controlBoard);
        Assert.Contains("class=\"action-queue\"", controlBoard);
        Assert.Contains("class=\"action-queue-row action-queue-row-alert\"", controlBoard);
        Assert.Contains("class=\"workboard-search\"", controlBoard);
        Assert.Contains("class=\"quick-actions\"", controlBoard);
        Assert.Matches("\\.action-queue-row\\s*\\{[\\s\\S]*?display: grid;[\\s\\S]*?grid-template-columns: minmax\\(12rem, 1fr\\) minmax\\(8rem, 1fr\\) auto", css);
        Assert.Matches("@media \\(max-width: 720px\\)[\\s\\S]*?\\.action-queue-row\\s*\\{[\\s\\S]*?grid-template-columns: 1fr auto", css);
        Assert.Matches("\\.workboard-search\\s*\\{[\\s\\S]*?display: grid;[\\s\\S]*?grid-template-columns: 16rem minmax\\(0, 1fr\\) auto", css);
        Assert.Matches("@media \\(max-width: 720px\\)[\\s\\S]*?\\.workboard-search\\s*\\{[\\s\\S]*?grid-template-columns: 1fr auto;[\\s\\S]*?width: 100%", css);
    }

    [Fact]
    public void Material_catalog_collapses_mobile_thickness_lists_without_changing_desktop_table()
    {
        var catalog = ReadPage("Materials", "Index.cshtml");
        var css = CssTestHelper.ReadEffectiveAppCss();

        Assert.Contains("class=\"material-thickness-table desktop-table\"", catalog);
        Assert.Contains("class=\"material-thickness-disclosure mobile-card\"", catalog);
        Assert.Contains("Available thicknesses", catalog);
        Assert.Contains("@material.Thicknesses.Count options", catalog);
        Assert.Contains("openAddThickness(this)", catalog);
        Assert.Matches("\\.material-thickness-disclosure > summary\\s*\\{[\\s\\S]*?display: flex", css);
        Assert.Matches("@media \\(max-width: 720px\\)[\\s\\S]*?\\.desktop-table\\s*\\{[\\s\\S]*?display: none", css);
        Assert.Matches("@media \\(max-width: 720px\\)[\\s\\S]*?\\.mobile-card\\s*\\{[\\s\\S]*?display: block", css);
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
