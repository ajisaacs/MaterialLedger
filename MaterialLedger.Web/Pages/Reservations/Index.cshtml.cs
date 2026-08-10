// MaterialLedger.Web/Pages/Reservations/Index.cshtml.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Reservations;

[Authorize]
public class IndexModel(MaterialLedgerDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public ReservationStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public ReservationEquipment? Equipment { get; set; }
    public List<ReservationQueueSection> QueueSections { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var query = db.MaterialReservations
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.MaterialSpec)
            .Include(x => x.InventoryItem)!.ThenInclude(x => x!.InventoryLocation)
            .Where(x => x.Status == ReservationStatus.Proposed
                || x.Status == ReservationStatus.Approved
                || x.Status == ReservationStatus.AwaitingCloseout);

        if (Status is not null) query = query.Where(x => x.Status == Status);
        if (Equipment is not null) query = query.Where(x => x.Equipment == Equipment);

        var reservations = await query
            .OrderByDescending(x => x.ApprovedAtUtc ?? x.CreatedAtUtc)
            .ThenByDescending(x => x.MaterialReservationId)
            .ToListAsync();

        QueueSections =
        [
            CreateSection(
                ReservationQueueKind.ApprovalNeeded,
                "Approval needed",
                "Review held material before it is released for cutting.",
                "No material holds need approval.",
                reservations.Where(x => x.Status == ReservationStatus.Proposed)),
            CreateSection(
                ReservationQueueKind.ReadyForCutting,
                "Ready for cutting",
                "Approved material is ready at its listed location.",
                "No material is ready for cutting.",
                reservations.Where(x => x.Status == ReservationStatus.Approved)),
            CreateSection(
                ReservationQueueKind.CutResultNeeded,
                "Cut result needed",
                "Record what physically remains after the cut.",
                "No cut results are waiting to be recorded.",
                reservations.Where(x => x.Status == ReservationStatus.AwaitingCloseout))
        ];
    }

    private static ReservationQueueSection CreateSection(
        ReservationQueueKind kind,
        string title,
        string description,
        string emptyMessage,
        IEnumerable<MaterialReservation> reservations) =>
        new(kind, title, description, emptyMessage, reservations.Select(ToQueueRow).ToList());

    private static ReservationQueueRow ToQueueRow(MaterialReservation reservation) => new(
        reservation,
        ReservationWorkReference.Describe(reservation),
        UsableSize(reservation),
        reservation.ApprovedAtUtc ?? reservation.CreatedAtUtc);


    private static string UsableSize(MaterialReservation reservation)
    {
        var item = reservation.InventoryItem;
        return item?.UsableWidthIn is not null && item.UsableLengthIn is not null
            ? $"{item.UsableWidthIn:0.###} × {item.UsableLengthIn:0.###} in"
            : "Usable size not recorded";
    }
}

public enum ReservationQueueKind { ApprovalNeeded, ReadyForCutting, CutResultNeeded }

public sealed record ReservationQueueSection(
    ReservationQueueKind Kind,
    string Title,
    string Description,
    string EmptyMessage,
    IReadOnlyList<ReservationQueueRow> Rows);

public sealed record ReservationQueueRow(
    MaterialReservation Reservation,
    string WorkReference,
    string UsableSize,
    DateTime TimestampUtc);
