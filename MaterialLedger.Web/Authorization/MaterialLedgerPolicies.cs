using Microsoft.AspNetCore.Authorization;

namespace MaterialLedger.Web.Authorization;

public static class MaterialLedgerPolicies
{
    public const string RequireProgrammerOrManager = nameof(RequireProgrammerOrManager);
    public const string RequireOperatorOrManager = nameof(RequireOperatorOrManager);
    public const string RequireManager = nameof(RequireManager);

    public static void AddMaterialLedgerPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(RequireProgrammerOrManager, p => p.RequireRole("Programmer", "Manager"));
        options.AddPolicy(RequireOperatorOrManager, p => p.RequireRole("Operator", "Manager"));
        options.AddPolicy(RequireManager, p => p.RequireRole("Manager"));
    }
}
