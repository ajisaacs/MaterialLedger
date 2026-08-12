using System.Text;
using System.Text.RegularExpressions;

namespace MaterialLedger.Tests;

internal static class CssTestHelper
{
    private static readonly Regex ImportPattern = new(
        """@import\s+url\(["']([^"']+)["']\);""",
        RegexOptions.Compiled);

    public static string ReadEffectiveAppCss()
    {
        var cssDirectory = Path.Combine(RepositoryRoot, "MaterialLedger.Web", "wwwroot", "css");
        var manifest = File.ReadAllText(Path.Combine(cssDirectory, "app.css"));

        var imports = ImportPattern.Matches(manifest);
        if (imports.Count == 0)
        {
            return manifest;
        }

        var effective = new StringBuilder();
        foreach (Match import in imports)
        {
            effective.AppendLine(File.ReadAllText(Path.Combine(cssDirectory, import.Groups[1].Value)));
        }

        return effective.ToString();
    }

    private static string RepositoryRoot => Path.GetFullPath("../../../../", AppContext.BaseDirectory);
}
