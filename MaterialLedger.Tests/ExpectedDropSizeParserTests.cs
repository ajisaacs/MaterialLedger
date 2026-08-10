using MaterialLedger.Web.Pages.Reservations;
using Xunit;

namespace MaterialLedger.Tests;

public sealed class ExpectedDropSizeParserTests
{
    [Theory]
    [InlineData("12.5x24", 12.5, 24)]
    [InlineData("12 1/2 x 24 3/8", 12.5, 24.375)]
    [InlineData("12-1/2X24-3/8", 12.5, 24.375)]
    public void TryParse_accepts_decimal_and_fractional_dimensions(string input, decimal expectedWidth, decimal expectedLength)
    {
        var parsed = ExpectedDropSizeParser.TryParse(input, out var width, out var length);

        Assert.True(parsed);
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedLength, length);
    }
}
