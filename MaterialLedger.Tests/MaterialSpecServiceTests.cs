using MaterialLedger.Data.Materials;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class MaterialSpecServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public MaterialSpecServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetOrCreateAsync_NewSpec_CreatesRow()
    {
        await using var db = _fixture.CreateContext();
        var service = new MaterialSpecService(db);

        var spec = await service.GetOrCreateAsync("Carbon Steel", "A36", null, 0.1875m, "A36 3/16in");

        Assert.True(spec.MaterialSpecId > 0);
        Assert.Equal("Carbon Steel", spec.MaterialFamily);
    }

    [Fact]
    public async Task GetOrCreateAsync_MatchingSpecCalledTwice_ReturnsSameRowWithoutDuplicating()
    {
        await using var db = _fixture.CreateContext();
        var service = new MaterialSpecService(db);

        var first = await service.GetOrCreateAsync("Stainless", "304", "#4", 0.125m, "304 #4 1/8in");
        var second = await service.GetOrCreateAsync("Stainless", "304", "#4", 0.125m, "304 #4 1/8in — duplicate attempt");

        Assert.Equal(first.MaterialSpecId, second.MaterialSpecId);
        var count = await db.MaterialSpecs.CountAsync(x => x.MaterialFamily == "Stainless" && x.Grade == "304");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetOrCreateAsync_WithGaugeLabel_StoresLabelOnRow()
    {
        await using var db = _fixture.CreateContext();
        var service = new MaterialSpecService(db);

        var spec = await service.GetOrCreateAsync("Stainless Steel", "410", null, 0.1875m, "Stainless Steel 410 — 3/16 (0.1875\")", gaugeLabel: "3/16");

        Assert.Equal("3/16", spec.GaugeLabel);
    }

    [Fact]
    public async Task GetOrCreateAsync_WithoutGaugeLabel_LeavesLabelNull()
    {
        await using var db = _fixture.CreateContext();
        var service = new MaterialSpecService(db);

        var spec = await service.GetOrCreateAsync("Stainless Steel", "410", null, 0.0625m, "Stainless Steel 410 0.0625 in");

        Assert.Null(spec.GaugeLabel);
    }

    [Fact]
    public async Task SearchAsync_FiltersByGradeSubstring()
    {
        await using var db = _fixture.CreateContext();
        var service = new MaterialSpecService(db);
        await service.GetOrCreateAsync("Aluminum", "5052", null, 0.25m, "5052 1/4in");
        await service.GetOrCreateAsync("Carbon Steel", "A36", null, 0.25m, "A36 1/4in");

        var results = await service.SearchAsync("5052");

        Assert.Single(results);
        Assert.Equal("Aluminum", results[0].MaterialFamily);
    }
}
