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
    public List<MaterialSpec> AvailableSpecs { get; private set; } = [];
    public string? Error { get; private set; }
    public string? Success { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(NewMaterial.MaterialFamily) || NewMaterial.ThicknessIn <= 0)
        {
            Error = "Material family and a positive thickness are required.";
            await LoadAsync();
            return Page();
        }

        var family = NewMaterial.MaterialFamily.Trim();
        var grade = Normalize(NewMaterial.Grade);
        var finish = Normalize(NewMaterial.Finish);
        var displayName = string.Join(" ", new[] { family, grade, finish, $"{NewMaterial.ThicknessIn:0.####} in" }.Where(x => !string.IsNullOrWhiteSpace(x)));
        await materialSpecs.GetOrCreateAsync(family, grade, finish, NewMaterial.ThicknessIn, displayName);
        Success = "Material is available for sheet entry.";
        NewMaterial = new();
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync() => AvailableSpecs = (await materialSpecs.SearchAsync(null)).ToList();
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public class NewMaterialInput
{
    public string MaterialFamily { get; set; } = string.Empty;
    public string? Grade { get; set; }
    public string? Finish { get; set; }
    public decimal ThicknessIn { get; set; }
}
