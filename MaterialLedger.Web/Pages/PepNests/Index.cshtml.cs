using MaterialLedger.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.PepNests;

[Authorize]
public class IndexModel(PepNestQueryService pepNests) : PageModel
{
    [BindProperty(SupportsGet = true)] public int Year { get; set; } = DateTime.UtcNow.Year;
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Customer { get; set; }
    public IReadOnlyList<PepNestSummary> Nests { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task OnGetAsync()
    {
        if (Year is < 2000 or > 2100) { Error = "Enter a four-digit nest year."; return; }
        try { Nests = (await pepNests.GetNestsAsync(Year, Status, Customer)).OrderByDescending(x => x.DateCreated).ToList(); }
        catch (HttpRequestException) { Error = "PEP nests could not be reached. Try again after the PEP API is available."; }
    }
}
