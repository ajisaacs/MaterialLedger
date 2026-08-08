using System.Security.Claims;
using MaterialLedger.Data.Exceptions;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Exceptions;

[Authorize]
public class IndexModel(MaterialExceptionService exceptions) : PageModel
{
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public string? ResolutionNotes { get; set; }

    public List<MaterialException> OpenExceptions { get; set; } = [];
    public List<MaterialException> ResolvedExceptions { get; set; } = [];
    public Dictionary<long, Guid> CorrelationIdsByExceptionId { get; set; } = [];
    public bool CanResolve { get; set; }
    public string? Error { get; set; }

    public async Task OnGetAsync()
    {
        await exceptions.ScanAsync();
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostResolveAsync(long id, Guid correlationId)
    {
        if (!User.IsInRole("Programmer") && !User.IsInRole("Manager"))
            return Forbid();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await exceptions.ResolveMaterialExceptionAsync(correlationId, id, ResolutionNotes, userId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationConflictException)
        {
            Error = ex.Message;
            await LoadAsync();
            return Page();
        }

        return RedirectToPage("Index");
    }

    private async Task LoadAsync()
    {
        OpenExceptions = await exceptions.GetOpenExceptionsAsync();
        ResolvedExceptions = await exceptions.GetRecentlyResolvedAsync();
        CorrelationIdsByExceptionId = OpenExceptions.ToDictionary(x => x.MaterialExceptionId, _ => Guid.NewGuid());
        CanResolve = User.IsInRole("Programmer") || User.IsInRole("Manager");
    }
}
