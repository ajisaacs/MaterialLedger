using MaterialLedger.Data.Materials;

namespace MaterialLedger.Web.Services;

public class MaterialSpecSeeder(MaterialSpecService materialSpecService)
{
    private sealed record SeedSpec(string Family, string? Grade, string? Finish, decimal ThicknessIn, string DisplayName);

    private sealed record GaugeEntry(string Label, decimal ThicknessIn);

    // Aluminum 5052 / 6061 share this gauge table in LaserQuote.
    private static readonly GaugeEntry[] AluminumGauges =
    [
        new("30ga", 0.0100m), new("28ga", 0.0126m), new("26ga", 0.0159m), new("24ga", 0.0201m),
        new("22ga", 0.0253m), new("20ga", 0.0320m), new("18ga", 0.0403m), new("16ga", 0.0508m),
        new("14ga", 0.0641m), new("13ga", 0.0720m), new("12ga", 0.0808m), new("11ga", 0.0910m),
        new("10ga", 0.1019m)
    ];

    // Steel A1008/CRS, A1011/HR, A1011/HRPO, and plain HRPO share this gauge table in LaserQuote.
    private static readonly GaugeEntry[] SteelGauges =
    [
        new("30ga", 0.0120m), new("28ga", 0.0149m), new("26ga", 0.0179m), new("24ga", 0.0239m),
        new("22ga", 0.0299m), new("20ga", 0.0359m), new("18ga", 0.0478m), new("16ga", 0.0598m),
        new("14ga", 0.0747m), new("13ga", 0.0897m), new("12ga", 0.1046m), new("11ga", 0.1196m),
        new("10ga", 0.1345m)
    ];

    private static readonly GaugeEntry[] StainlessGauges =
    [
        new("30ga", 0.0125m), new("28ga", 0.0156m), new("26ga", 0.0187m), new("24ga", 0.0250m),
        new("22ga", 0.0313m), new("20ga", 0.0375m), new("18ga", 0.0500m), new("16ga", 0.0625m),
        new("14ga", 0.0781m), new("13ga", 0.0938m), new("12ga", 0.1094m), new("11ga", 0.1250m),
        new("10ga", 0.1406m)
    ];

    // Steel A36 and A572 GR50 share this plate-thickness table in LaserQuote.
    private static readonly GaugeEntry[] PlateThicknesses =
    [
        new("3/16", 0.1875m), new("3ga", 0.2391m), new("1/4", 0.2500m), new("5/16", 0.3125m),
        new("3/8", 0.3750m), new("1/2", 0.5000m), new("5/8", 0.6250m), new("3/4", 0.7500m),
        new("1", 1.0000m)
    ];

    private static IEnumerable<SeedSpec> ForMaterial(string family, string? grade, string? finish, GaugeEntry[] gauges) =>
        gauges.Select(g => new SeedSpec(
            family, grade, finish, g.ThicknessIn,
            $"{family} {grade}{(finish is null ? "" : " " + finish)} — {g.Label} ({g.ThicknessIn:0.0000}\")"));

    private static readonly SeedSpec[] SeedSpecs =
    [
        .. ForMaterial("Aluminum", "5052", null, AluminumGauges),
        .. ForMaterial("Aluminum", "6061", null, AluminumGauges),
        .. ForMaterial("Stainless Steel", "304", null, StainlessGauges),
        .. ForMaterial("Steel", "A1008", "CRS", SteelGauges),
        .. ForMaterial("Steel", "A1011", "HR", SteelGauges),
        .. ForMaterial("Steel", "A1011", "HRPO", SteelGauges),
        .. ForMaterial("Steel", "HRPO", null, SteelGauges),
        .. ForMaterial("Steel", "A36", null, PlateThicknesses),
        .. ForMaterial("Steel", "A572 GR50", null, PlateThicknesses),
        new SeedSpec("Steel", "Floor Plate", null, 0.1875m, "Steel Floor Plate — 3/16 (0.1875\")")
    ];

    public async Task SeedAsync()
    {
        foreach (var s in SeedSpecs)
            await materialSpecService.GetOrCreateAsync(s.Family, s.Grade, s.Finish, s.ThicknessIn, s.DisplayName);
    }
}
