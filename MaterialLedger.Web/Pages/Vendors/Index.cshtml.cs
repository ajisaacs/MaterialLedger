using MaterialLedger.Data.Vendors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Vendors;

[Authorize]
public class IndexModel(VendorQueryService vendors) : PageModel
{
    public IReadOnlyList<MaterialLedger.Data.Models.Vendor> Vendors { get; private set; } = [];
    public async Task OnGetAsync() => Vendors = await vendors.GetVendorsAsync();
}
