using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Receiving;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class SuccessModel : PageModel
{
    [BindProperty(SupportsGet = true)] public string Code { get; set; } = string.Empty;
    public void OnGet() { }
}
