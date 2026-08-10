using MaterialLedger.Data.Materials;
using MaterialLedger.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class MaterialSpecSeederTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public MaterialSpecSeederTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SeedAsync_EmptyDatabase_CreatesOneHundredTenSpecs()
    {
        await using var db = _fixture.CreateContext();
        var seeder = new MaterialSpecSeeder(new MaterialSpecService(db));

        await seeder.SeedAsync();

        var count = await db.MaterialSpecs.CountAsync();
        Assert.Equal(110, count);
    }

    [Fact]
    public async Task SeedAsync_RunTwice_StaysIdempotent()
    {
        await using var db = _fixture.CreateContext();
        var seeder = new MaterialSpecSeeder(new MaterialSpecService(db));

        await seeder.SeedAsync();
        await seeder.SeedAsync();

        var count = await db.MaterialSpecs.CountAsync();
        Assert.Equal(110, count);
    }

    [Fact]
    public async Task SeedAsync_A36ThreeSixteenth_HasExpectedThicknessAndNoFinish()
    {
        await using var db = _fixture.CreateContext();
        var seeder = new MaterialSpecSeeder(new MaterialSpecService(db));

        await seeder.SeedAsync();

        var spec = await db.MaterialSpecs.SingleAsync(x =>
            x.MaterialFamily == "Steel" && x.Grade == "A36" && x.ThicknessIn == 0.1875m);
        Assert.Null(spec.Finish);
        Assert.Equal("Steel A36 — 3/16 (0.1875\")", spec.DisplayName);
    }

    [Fact]
    public async Task SeedAsync_Aluminum5052ThirtyGauge_HasExpectedThickness()
    {
        await using var db = _fixture.CreateContext();
        var seeder = new MaterialSpecSeeder(new MaterialSpecService(db));

        await seeder.SeedAsync();

        var spec = await db.MaterialSpecs.SingleAsync(x =>
            x.MaterialFamily == "Aluminum" && x.Grade == "5052" && x.ThicknessIn == 0.0100m);
        Assert.Equal("Aluminum 5052 — 30ga (0.0100\")", spec.DisplayName);
    }

    [Fact]
    public async Task SeedAsync_SteelA1011HrpoSixteenGauge_SplitsGradeAndFinish()
    {
        await using var db = _fixture.CreateContext();
        var seeder = new MaterialSpecSeeder(new MaterialSpecService(db));

        await seeder.SeedAsync();

        var spec = await db.MaterialSpecs.SingleAsync(x =>
            x.MaterialFamily == "Steel" && x.Grade == "A1011" && x.Finish == "HRPO" && x.ThicknessIn == 0.0598m);
        Assert.Equal("Steel A1011 HRPO — 16ga (0.0598\")", spec.DisplayName);
    }

    [Fact]
    public async Task SeedAsync_FloorPlate_HasNullFinish()
    {
        await using var db = _fixture.CreateContext();
        var seeder = new MaterialSpecSeeder(new MaterialSpecService(db));

        await seeder.SeedAsync();

        var spec = await db.MaterialSpecs.SingleAsync(x =>
            x.MaterialFamily == "Steel" && x.Grade == "Floor Plate");
        Assert.Null(spec.Finish);
        Assert.Equal(0.1875m, spec.ThicknessIn);
    }

    [Fact]
    public async Task SeedAsync_HrpoAndA1011Hrpo_AreDistinctRowsNotDeduped()
    {
        await using var db = _fixture.CreateContext();
        var seeder = new MaterialSpecSeeder(new MaterialSpecService(db));

        await seeder.SeedAsync();

        var plainHrpoCount = await db.MaterialSpecs.CountAsync(x =>
            x.MaterialFamily == "Steel" && x.Grade == "HRPO" && x.Finish == null);
        var a1011HrpoCount = await db.MaterialSpecs.CountAsync(x =>
            x.MaterialFamily == "Steel" && x.Grade == "A1011" && x.Finish == "HRPO");

        Assert.Equal(13, plainHrpoCount);
        Assert.Equal(13, a1011HrpoCount);
    }
}
