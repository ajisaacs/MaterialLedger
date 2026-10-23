using MaterialLedger.Data.Models;

namespace MaterialLedger.Web.Shared;

public static class ReservationWorkReference
{
    public static string Describe(MaterialReservation reservation)
    {
        if (!string.IsNullOrWhiteSpace(reservation.ShopOpsJobId)) return $"Job {reservation.ShopOpsJobId}";
        if (!string.IsNullOrWhiteSpace(reservation.DrawingId))
            return string.IsNullOrWhiteSpace(reservation.DrawingRevision)
                ? $"Drawing {reservation.DrawingId}"
                : $"Drawing {reservation.DrawingId} rev {reservation.DrawingRevision}";
        if (!string.IsNullOrWhiteSpace(reservation.NestName)) return $"Nest {reservation.NestName}";
        if (!string.IsNullOrWhiteSpace(reservation.PepNestName))
            return reservation.PepNestYear is null
                ? $"Nest {reservation.PepNestName}"
                : $"Nest {reservation.PepNestYear}: {reservation.PepNestName}";
        return "Unassigned material hold";
    }

    /// <summary>
    /// App-relative path to the OpenNest nest page, or null for holds without an OpenNest nest
    /// (legacy PEP holds link through their own page).
    /// </summary>
    public static string? NestPath(MaterialReservation reservation) =>
        reservation.OpenNestNestId is { } nestId ? $"~/Nests/{nestId}" : null;
}