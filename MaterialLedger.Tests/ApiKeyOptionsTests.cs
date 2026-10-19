using MaterialLedger.Web.Api;
using Xunit;

namespace MaterialLedger.Tests;

public class ApiKeyOptionsTests
{
    private static readonly ApiKeyOptions Options = new()
    {
        Keys = new(StringComparer.OrdinalIgnoreCase) { ["CutSend"] = "cutsend-key", ["Unused"] = "" },
    };

    [Fact]
    public void FindClient_NamesTheClientWhoseKeyMatches() =>
        Assert.Equal("CutSend", Options.FindClient("cutsend-key"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CUTSEND-KEY")]
    [InlineData("cutsend-key ")]
    public void FindClient_RejectsAnythingElse(string? presented) =>
        Assert.Null(Options.FindClient(presented));

    [Fact]
    public void FindClient_NeverMatchesABlankKey() =>
        Assert.Null(new ApiKeyOptions { Keys = new() { ["CutSend"] = "" } }.FindClient(" "));
}
