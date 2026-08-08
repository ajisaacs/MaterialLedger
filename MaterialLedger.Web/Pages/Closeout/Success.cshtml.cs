using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Closeout;

[Authorize(Policy = MaterialLedgerPolicies.RequireOperatorOrManager)]
public class SuccessModel : PageModel
{
    public long ReservationId { get; set; }
    public string? RemnantCode { get; set; }

    public void OnGet(long id, string? remnantCode)
    {
        ReservationId = id;
        RemnantCode = remnantCode;
    }
}
