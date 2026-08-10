using Xunit;

namespace MaterialLedger.Tests;

public sealed class DependentSelectScriptTests
{
    [Fact]
    public void Shared_script_defines_a_reusable_dependent_select_filter()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "js", "dependent-select.js"));

        Assert.Contains("function initDependentSelect(trigger, dependent, datasetKey)", script);
        Assert.Contains("dependent.appendChild(option)", script);
        Assert.Contains("dependent.disabled = !trigger.value", script);
        Assert.Contains("option.remove()", script);
        Assert.DoesNotContain("option.hidden", script);
    }

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
