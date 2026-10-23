using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Nests;

[Authorize]
public class IndexModel(OpenNestClient openNest) : PageModel
{
    // Values are OpenNest's camelCase status names; it rejects anything else with 400.
    public static readonly IReadOnlyList<(string Value, string Label)> StatusOptions =
    [
        ("draft", "Draft"), ("quote", "Quote"), ("toBeCut", "To Be Cut"), ("hasBeenCut", "Has Been Cut"),
    ];

    [BindProperty(SupportsGet = true)] public string? Status { get; set; } = "toBeCut";
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    public IReadOnlyList<OpenNestRecord> Nests { get; private set; } = [];
    public string? Error { get; private set; }

    public static string StatusLabel(string? status) =>
        StatusOptions.FirstOrDefault(x => string.Equals(x.Value, status, StringComparison.OrdinalIgnoreCase)).Label ?? status ?? "";

    public static string MaterialText(OpenNestRecord nest) =>
        string.Join(" ", new[] { nest.Material, nest.MaterialGrade, $"{nest.Thickness:0.####}\"" }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public async Task OnGetAsync()
    {
        if (Status is not null && !StatusOptions.Any(x => x.Value == Status)) { Error = "Choose a status from the list."; return; }
        try { Nests = (await openNest.QueryNestsAsync(new OpenNestQuery(Status, Search, Limit: 100), HttpContext?.RequestAborted ?? default)).Items; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Error = "OpenNest nests could not be reached."; }
    }
}
