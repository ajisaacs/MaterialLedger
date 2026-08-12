using MaterialLedger.Data.Models;

namespace MaterialLedger.Web.Pages.Shared;

public static class ReservationWorkReference
{
    public static string Describe(MaterialReservation reservation)
    {
        if (!string.IsNullOrWhiteSpace(reservation.ShopOpsJobId))
            return reservation.ShopOpsJobId;
        if (!string.IsNullOrWhiteSpace(reservation.DrawingId))
            return string.IsNullOrWhiteSpace(reservation.DrawingRevision)
                ? reservation.DrawingId
                : $"{reservation.DrawingId} rev {reservation.DrawingRevision}";
        if (!string.IsNullOrWhiteSpace(reservation.PepNestName))
            return reservation.PepNestYear is null ? reservation.PepNestName : $"{reservation.PepNestYear} {reservation.PepNestName}";
        return "No work reference recorded";
    }
}
