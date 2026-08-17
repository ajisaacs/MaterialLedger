using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Data.Vendors;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Vendors;

[Authorize]
public class DetailsModel(MaterialLedgerDbContext db, VendorQueryService queries, VendorService vendors) : PageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty(SupportsGet = true)] public long? VendorPartId { get; set; }
    [BindProperty] public NewPartInput NewPart { get; set; } = new();
    public Vendor? Vendor { get; private set; }
    public List<VendorPart> Parts { get; private set; } = [];
    public IReadOnlyList<PartActualPricePoint> Actuals { get; private set; } = [];
    public IReadOnlyList<PartQuotedPricePoint> Quotes { get; private set; } = [];
    public PartPriceSummary? Summary { get; private set; }
    public string? Error { get; private set; }
    public async Task<IActionResult> OnGetAsync() { if (!await LoadAsync()) return NotFound(); return Page(); }
    public async Task<IActionResult> OnPostAddPartAsync() { try { var userId=int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value); var part=await vendors.CreateVendorPartAsync(Guid.NewGuid(),new(Id,NewPart.PartNumber,NewPart.Description,NewPart.MinimumOrderQty,NewPart.Unit),userId); return RedirectToPage(new { id=Id, vendorPartId=part.VendorPartId }); } catch(Exception ex) when(ex is ArgumentException or InvalidOperationException) { Error=ex.Message; if(!await LoadAsync()) return NotFound(); return Page(); } }
    private async Task<bool> LoadAsync() { Vendor=await db.Vendors.AsNoTracking().SingleOrDefaultAsync(x=>x.VendorId==Id); if(Vendor is null) return false; Parts=await db.VendorParts.AsNoTracking().Where(x=>x.VendorId==Id).OrderBy(x=>x.PartNumber).ToListAsync(); if(VendorPartId is { } partId && Parts.Any(x=>x.VendorPartId==partId)) { Actuals=await queries.GetActualPriceHistoryAsync(partId); Quotes=await queries.GetQuotedPriceHistoryAsync(partId); Summary=await queries.GetPriceSummaryAsync(partId); } return true; }
}
public class NewPartInput { public string PartNumber { get; set; } = string.Empty; public string? Description { get; set; } public decimal? MinimumOrderQty { get; set; } public string? Unit { get; set; } }
