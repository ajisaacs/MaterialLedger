using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MaterialLedger.Web.Pages.Reservations;

public static class ExpectedDropSizeParser
{
    private static readonly Regex FractionRegex = new(
        @"((?<whole>\d+)(\s+|-))?(?<fraction>\d+/\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string? input, out decimal widthIn, out decimal lengthIn)
    {
        widthIn = 0;
        lengthIn = 0;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var dimensions = ReplaceFractionsWithDecimals(input).Split('x', StringSplitOptions.TrimEntries);
        return dimensions.Length == 2
            && decimal.TryParse(dimensions[0], NumberStyles.Number, CultureInfo.InvariantCulture, out widthIn)
            && decimal.TryParse(dimensions[1], NumberStyles.Number, CultureInfo.InvariantCulture, out lengthIn)
            && widthIn > 0
            && lengthIn > 0;
    }

    private static string ReplaceFractionsWithDecimals(string input)
    {
        var result = new StringBuilder(input);
        foreach (Match match in FractionRegex.Matches(input).Cast<Match>().OrderByDescending(match => match.Index))
        {
            var fraction = match.Groups["fraction"].Value.Split('/');
            if (!decimal.TryParse(fraction[0], NumberStyles.None, CultureInfo.InvariantCulture, out var numerator)
                || !decimal.TryParse(fraction[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator)
                || denominator == 0)
                return input;

            var whole = match.Groups["whole"].Success
                ? decimal.Parse(match.Groups["whole"].Value, CultureInfo.InvariantCulture)
                : 0m;
            var value = whole + decimal.Round(numerator / denominator, 8);
            result.Remove(match.Index, match.Length);
            result.Insert(match.Index, value.ToString(CultureInfo.InvariantCulture));
        }

        return result.ToString().Replace('X', 'x');
    }
}
