using MaterialLedger.Data.Materials;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Materials;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class IndexModel(MaterialSpecService materialSpecs) : PageModel
{
    [BindProperty] public NewMaterialInput NewMaterial { get; set; } = new();
    public List<MaterialCatalogGroup> MaterialGroups { get; private set; } = [];
    public string? Error { get; private set; }
    public string? Success { get; private set; }
    public bool ShowAddMaterialDialog { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(NewMaterial.MaterialFamily) || NewMaterial.ThicknessIn <= 0)
        {
            Error = "Material family and a positive thickness are required.";
            ShowAddMaterialDialog = true;
            await LoadAsync();
            return Page();
        }

        var family = NewMaterial.MaterialFamily.Trim();
        var grade = Normalize(NewMaterial.Grade);
        var finish = Normalize(NewMaterial.Finish);
        var gaugeLabel = Normalize(NewMaterial.GaugeLabel);
        var displayName = string.Join(" ", new[] { family, grade, finish, $"{NewMaterial.ThicknessIn:0.####} in" }.Where(x => !string.IsNullOrWhiteSpace(x)));
        await materialSpecs.GetOrCreateAsync(family, grade, finish, NewMaterial.ThicknessIn, displayName, gaugeLabel);
        Success = "Material is available for sheet entry.";
        NewMaterial = new();
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var specs = await materialSpecs.SearchAsync(null);
        MaterialGroups = specs
            .GroupBy(spec => new { spec.MaterialFamily, spec.Grade, spec.Finish })
            .Select(group => new MaterialCatalogGroup(
                string.Join(" ", new[] { group.Key.MaterialFamily, group.Key.Grade, group.Key.Finish }.Where(value => !string.IsNullOrWhiteSpace(value))),
                group.Key.MaterialFamily,
                group.Key.Grade,
                group.Key.Finish,
                group.OrderBy(spec => spec.ThicknessIn).ToList()))
            .ToList();
    }
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record MaterialCatalogGroup(
    string Name,
    string MaterialFamily,
    string? Grade,
    string? Finish,
    IReadOnlyList<MaterialSpec> Thicknesses);

public class NewMaterialInput
{
    public string MaterialFamily { get; set; } = string.Empty;
    public string? Grade { get; set; }
    public string? Finish { get; set; }
    public decimal ThicknessIn { get; set; }
    public string? GaugeLabel { get; set; }
}
