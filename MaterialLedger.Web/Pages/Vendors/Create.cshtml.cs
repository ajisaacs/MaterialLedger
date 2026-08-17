using System.Security.Claims;
using MaterialLedger.Data.Vendors;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Vendors;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class CreateModel(VendorService vendors) : PageModel
{
    [BindProperty] public VendorInput Input { get; set; } = new();
    public string? Error { get; private set; }
    public async Task<IActionResult> OnPostAsync()
    {
        try { var id = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value); var vendor = await vendors.CreateVendorAsync(Guid.NewGuid(), Input.ToCreate(), id); return RedirectToPage("Details", new { id = vendor.VendorId }); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Error = ex.Message; return Page(); }
    }
}

public class VendorInput
{
    public string Name { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Notes { get; set; }
    public CreateVendorRequest ToCreate() => new(Name, ContactName, ContactEmail, ContactPhone, Notes);
    public UpdateVendorRequest ToUpdate(bool active) => new(Name, ContactName, ContactEmail, ContactPhone, Notes, active);
}
