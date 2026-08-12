using Xunit;

namespace MaterialLedger.Tests;

public sealed class QuickSheetEntryScriptTests
{
    [Fact]
    public void Script_intercepts_submit_and_fetches_instead_of_navigating()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "js", "quick-sheet-entry.js"));

        Assert.Contains("function initAddSheetForm(dialog)", script);
        Assert.Contains("event.preventDefault()", script);
        Assert.Contains("fetch(form.action", script);
        Assert.Contains("new FormData(form)", script);
    }

    [Fact]
    public void Script_resets_slot_and_material_but_leaves_rack_selection_on_success()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "js", "quick-sheet-entry.js"));

        Assert.Contains("slotSelect.value = ''", script);
        Assert.Contains("materialSelect.value = ''", script);
        Assert.DoesNotContain("rackSelect.value = ''", script);
        Assert.Contains("slotSelect.focus()", script);
    }

    [Fact]
    public void Script_appends_a_running_list_row_on_success_and_clears_it_when_the_dialog_closes()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "js", "quick-sheet-entry.js"));

        Assert.Contains("logList.appendChild(row)", script);
        Assert.Contains("dialog.addEventListener('close'", script);
        Assert.Contains("logList.replaceChildren()", script);
    }

    [Fact]
    public void Index_page_loads_the_script_and_wires_it_to_the_dialog()
    {
        var page = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "Pages", "Index.cshtml"));

        Assert.Contains("<script src=\"~/js/quick-sheet-entry.js\" asp-append-version=\"true\"></script>", page);
        Assert.Contains("initAddSheetForm(document.getElementById('add-sheet-dialog'))", page);
    }

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
