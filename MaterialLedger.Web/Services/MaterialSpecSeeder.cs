using System.Globalization;
using MaterialLedger.Data.Materials;

namespace MaterialLedger.Web.Services;

public class MaterialSpecSeeder(MaterialSpecService materialSpecService)
{
    private sealed record SeedSpec(string Family, string? Grade, string? Finish, decimal ThicknessIn, string DisplayName, string GaugeLabel);

    private static IEnumerable<SeedSpec> ForMaterial(string family, string? grade, string? finish, GaugeTable.Entry[] gauges) =>
        gauges.Select(g => new SeedSpec(
            family, grade, finish, g.ThicknessIn,
            ((FormattableString)$"{family} {grade}{(finish is null ? "" : " " + finish)} — {g.Label} ({g.ThicknessIn:0.0000}\")").ToString(CultureInfo.InvariantCulture),
            g.Label));

    private static readonly SeedSpec[] SeedSpecs =
    [
        .. ForMaterial("Aluminum", "5052", null, GaugeTable.Aluminum),
        .. ForMaterial("Aluminum", "6061", null, GaugeTable.Aluminum),
        .. ForMaterial("Stainless Steel", "304", null, GaugeTable.Stainless),
        .. ForMaterial("Stainless Steel", "304", null, GaugeTable.FractionalPlate),
        .. ForMaterial("Stainless Steel", "316", null, GaugeTable.Stainless),
        .. ForMaterial("Stainless Steel", "316", null, GaugeTable.FractionalPlate),
        .. ForMaterial("Steel", "A1008", "CRS", GaugeTable.Steel),
        .. ForMaterial("Steel", "A1011", "HR", GaugeTable.Steel),
        .. ForMaterial("Steel", "A1011", "HRPO", GaugeTable.Steel),
        .. ForMaterial("Steel", "HRPO", null, GaugeTable.Steel),
        .. ForMaterial("Steel", "A36", null, GaugeTable.Plate),
        .. ForMaterial("Steel", "A572 GR50", null, GaugeTable.Plate),
        new SeedSpec("Steel", "Floor Plate", null, 0.1875m, "Steel Floor Plate — 3/16 (0.1875\")", "3/16")
    ];

    public async Task SeedAsync()
    {
        foreach (var s in SeedSpecs)
            await materialSpecService.GetOrCreateAsync(s.Family, s.Grade, s.Finish, s.ThicknessIn, s.DisplayName, s.GaugeLabel);
    }
}
