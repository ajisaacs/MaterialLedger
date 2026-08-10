using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Closeout;

[Authorize(Policy = MaterialLedgerPolicies.RequireOperatorOrManager)]
public class SuccessModel : PageModel
{
    [BindProperty(SupportsGet = true)] public ReservationStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public ReservationEquipment? Equipment { get; set; }
    public long ReservationId { get; set; }
    public string? RemnantCode { get; set; }
    public long? RemnantItemId { get; set; }

    public void OnGet(long id, string? remnantCode, long? remnantItemId)
    {
        ReservationId = id;
        RemnantCode = remnantCode;
        RemnantItemId = remnantItemId;
    }
}
