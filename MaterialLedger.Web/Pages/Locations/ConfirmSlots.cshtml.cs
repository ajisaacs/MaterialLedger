using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Locations;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Locations;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class ConfirmSlotsModel(MaterialLedgerDbContext db, LocationSlotService slotService) : PageModel
{
    [BindProperty] public string Code { get; set; } = string.Empty;
    [BindProperty] public int SlotCount { get; set; } = 1;
    [BindProperty] public int StartingNumber { get; set; } = 1;
    [BindProperty] public Guid CorrelationId { get; set; }

    public InventoryLocation? Rack { get; set; }
    public string SlotPrefix { get; set; } = "S";
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(string code)
    {
        Rack = await db.InventoryLocations.FirstOrDefaultAsync(x => x.LocationCode == code);
        if (Rack is null)
            return NotFound();
        Code = code;
        SlotPrefix = PrefixFor(Rack.StorageMode);
        CorrelationId = Guid.NewGuid();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Rack = await db.InventoryLocations.FirstOrDefaultAsync(x => x.LocationCode == Code);
        if (Rack is null)
            return NotFound();
        SlotPrefix = PrefixFor(Rack.StorageMode);

        if (SlotCount is < 1 or > 200)
        {
            Error = "Slot count must be between 1 and 200.";
            return Page();
        }

        if (StartingNumber < 1)
        {
            Error = "Starting number must be at least 1.";
            return Page();
        }

        var slots = Enumerable.Range(StartingNumber, SlotCount)
            .Select(n => new SlotDefinition($"{SlotPrefix}{n:D2}", $"{SlotPrefix}{n:D2}", n))
            .ToList();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await slotService.ConfirmLocationSlotsAsync(CorrelationId, Code, slots, userId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or LocationSlotConflictException
            or MaterialLedger.Data.Idempotency.OperationConflictException)
        {
            Error = ex is MaterialLedger.Data.Idempotency.OperationConflictException
                ? "This form was already submitted with different values — please review and resubmit."
                : ex.Message;
            return Page();
        }

        return RedirectToPage("Details", new { code = Code });
    }

    private static string PrefixFor(InventoryStorageMode mode) => mode switch
    {
        InventoryStorageMode.RolloutShelf => "S",
        InventoryStorageMode.VerticalDropBay => "B",
        InventoryStorageMode.FloorStaging => "P",
        InventoryStorageMode.Other => "S",
        _ => "S"
    };
}
