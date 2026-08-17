using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Vendors;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Vendors;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class EditModel(MaterialLedgerDbContext db, VendorService vendors) : PageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty] public VendorInput Input { get; set; } = new();
    [BindProperty] public bool IsActive { get; set; }
    public string? Error { get; private set; }
    public async Task<IActionResult> OnGetAsync() { var v = await db.Vendors.FindAsync(Id); if (v is null) return NotFound(); Input = new() { Name=v.Name, ContactName=v.ContactName, ContactEmail=v.ContactEmail, ContactPhone=v.ContactPhone, Notes=v.Notes }; IsActive=v.IsActive; return Page(); }
    public async Task<IActionResult> OnPostAsync() { try { var userId=int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value); await vendors.UpdateVendorAsync(Id, Input.ToUpdate(IsActive), userId); return RedirectToPage("Details",new { id=Id }); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Error=ex.Message; return Page(); } }
}
