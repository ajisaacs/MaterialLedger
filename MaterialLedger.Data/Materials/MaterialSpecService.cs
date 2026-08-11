using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Materials;

public class MaterialSpecService(MaterialLedgerDbContext db)
{
    public async Task<IReadOnlyList<MaterialSpec>> SearchAsync(string? query)
    {
        var specs = db.MaterialSpecs.Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            specs = specs.Where(x =>
                EF.Functions.Like(x.MaterialFamily, $"%{q}%") ||
                (x.Grade != null && EF.Functions.Like(x.Grade, $"%{q}%")) ||
                EF.Functions.Like(x.DisplayName, $"%{q}%"));
        }
        return await specs.OrderBy(x => x.MaterialFamily).ThenBy(x => x.Grade).ThenBy(x => x.ThicknessIn).ToListAsync();
    }

    public async Task<MaterialSpec> GetOrCreateAsync(string materialFamily, string? grade, string? finish, decimal thicknessIn, string displayName, string? gaugeLabel = null)
    {
        var existing = await db.MaterialSpecs.FirstOrDefaultAsync(x =>
            x.MaterialFamily == materialFamily && x.Grade == grade && x.Finish == finish && x.ThicknessIn == thicknessIn);
        if (existing is not null)
            return existing;

        var spec = new MaterialSpec
        {
            MaterialFamily = materialFamily,
            Grade = grade,
            Finish = finish,
            ThicknessIn = thicknessIn,
            DisplayName = displayName,
            GaugeLabel = gaugeLabel,
            IsActive = true
        };
        db.MaterialSpecs.Add(spec);
        try
        {
            await db.SaveChangesAsync();
            return spec;
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent insert of the same normalized key (unique index on
            // NormalizedKey) — the other insert won, so detach ours and return the one that landed.
            db.Entry(spec).State = EntityState.Detached;
            return await db.MaterialSpecs.FirstAsync(x =>
                x.MaterialFamily == materialFamily && x.Grade == grade && x.Finish == finish && x.ThicknessIn == thicknessIn);
        }
    }
}
