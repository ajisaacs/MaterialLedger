using System.Globalization;

namespace MaterialLedger.Web.Shared;

public static class DisplayFormat
{
    public static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static string LocalDateTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Thickness(decimal thicknessIn, string? gaugeLabel) =>
        (gaugeLabel is null ? "" : $"{gaugeLabel} — ") + thicknessIn.ToString("0.####", CultureInfo.InvariantCulture) + " in";
}
