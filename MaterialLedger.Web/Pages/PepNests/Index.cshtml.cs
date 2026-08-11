using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.PepNests;

[Authorize]
public class IndexModel(PepNestQueryService pepNests) : PageModel
{
    public static readonly IReadOnlyList<string> StatusOptions =
    [
        "To be cut", "Quote", "Has been cut", "Hold",
        "Quote, accepted, to be cut", "Quote, accepted, has been cut",
        "Quote, accepted, hold", "Quote, accepted, moved to wip",
        "Quote, rejected", "Quote, timed out", "Deleted", "N/A",
    ];

    [BindProperty(SupportsGet = true)] public int Year { get; set; } = DateTime.UtcNow.Year;
    [BindProperty(SupportsGet = true)] public string? Status { get; set; } = "To be cut";
    [BindProperty(SupportsGet = true)] public string? Customer { get; set; }
    public IReadOnlyList<PepNestSummary> Nests { get; private set; } = [];
    public IReadOnlyList<string> CustomerOptions { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task OnGetAsync()
    {
        if (Year is < 2000 or > 2100) { Error = "Enter a four-digit nest year."; return; }
        try { Nests = (await pepNests.GetNestsAsync(Year, Status, Customer)).OrderByDescending(x => x.DateCreated).ToList(); }
        catch (HttpRequestException) { Error = "PEP nests could not be reached. Try again after the PEP API is available."; }

        try { CustomerOptions = (await pepNests.GetFilterOptionsAsync(Year)).Customers; }
        catch (HttpRequestException) { }
    }
}
