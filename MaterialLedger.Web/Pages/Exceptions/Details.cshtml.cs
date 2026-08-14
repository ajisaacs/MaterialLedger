// MaterialLedger.Web/Pages/Exceptions/Details.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Exceptions;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Exceptions;

[Authorize]
public class DetailsModel(MaterialExceptionService exceptions, MaterialLedgerDbContext db) : PageModel
{
    public MaterialException? Exception { get; set; }
    public bool CanResolve { get; set; }
    public string? Error { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public string? ResolutionNotes { get; set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        Exception = await db.MaterialExceptions
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Include(x => x.MaterialReservation)
            .Include(x => x.OpenedByUser)
            .Include(x => x.ResolvedByUser)
            .FirstOrDefaultAsync(x => x.MaterialExceptionId == id);

        if (Exception is null)
            return NotFound();

        CanResolve = User.IsInRole("Programmer") || User.IsInRole("Manager");
        CorrelationId = Guid.NewGuid();
        return Page();
    }

    public async Task<IActionResult> OnPostResolveAsync(long id)
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager"))
            return Forbid();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await exceptions.ResolveMaterialExceptionAsync(CorrelationId, id, ResolutionNotes, userId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationConflictException or DbUpdateException)
        {
            Error = ex is DbUpdateException ? "This exception was changed by someone else — refresh and try again." : ex.Message;
            return Page();
        }

        return RedirectToPage("Index");
    }
}
