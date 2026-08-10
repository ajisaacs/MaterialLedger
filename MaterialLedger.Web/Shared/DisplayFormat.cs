using System.Globalization;

namespace MaterialLedger.Web.Shared;

public static class DisplayFormat
{
    public static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
