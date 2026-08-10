# Import LaserQuote Material Specs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Seed `MaterialSpecs` with the 110 material/gauge/thickness combinations LaserQuote already uses, as a one-time static import with no ongoing sync.

**Architecture:** A new `MaterialSpecSeeder` in `MaterialLedger.Web/Services/`, following the existing `LocationSeeder` pattern exactly — a static list of seed rows, run idempotently on every app boot via `MaterialSpecService.GetOrCreateAsync` (which already dedupes on `MaterialFamily`+`Grade`+`Finish`+`ThicknessIn` and handles the `NormalizedKey` unique-index race). No schema changes.

**Tech Stack:** .NET 8, EF Core, xUnit, `IClassFixture<SqlServerTestDatabaseFixture>` integration tests (real throwaway SQL Server DB, no mocking — this codebase's established pattern per `MaterialSpecServiceTests`/`LocationSeederTests`).

## Global Constraints

- No `MaterialSpecExternalMapping` rows, no LaserQuote spec IDs stored anywhere — this is a one-time seed, not a tracked integration (design doc "Non-goals").
- No live call to LaserQuote at runtime — seed data is a static snapshot embedded in source.
- No tolerance values stored — `MaterialSpec` has no field for them.
- `DisplayName` format: `"{Family} {Grade}{ Finish?} — {gauge label} ({thickness:0.0000}\")"`.
- Exactly 110 rows on first run; re-running `SeedAsync()` must be a no-op (idempotent).

---

### Task 1: `MaterialSpecSeeder` with seed data and wiring

**Files:**
- Create: `MaterialLedger.Web/Services/MaterialSpecSeeder.cs`
- Modify: `MaterialLedger.Web/Program.cs` (register + invoke seeder at startup)
- Test: `MaterialLedger.Tests/MaterialSpecSeederTests.cs`

**Interfaces:**
- Consumes: `MaterialLedger.Data.Materials.MaterialSpecService.GetOrCreateAsync(string materialFamily, string? grade, string? finish, decimal thicknessIn, string displayName)` → `Task<MaterialSpec>` (existing, unchanged).
- Produces: `MaterialSpecSeeder(MaterialSpecService materialSpecService)` with `public async Task SeedAsync()`, no return value. Later tasks (none in this plan) would consume this the same way `LocationSeeder`/`AppUserSeeder` are consumed from `Program.cs`.

- [ ] **Step 1: Write the failing tests**

Create `MaterialLedger.Tests/MaterialSpecSeederTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MaterialSpecSeederTests"`
Expected: build error — `MaterialSpecSeeder` does not exist yet.

- [ ] **Step 3: Implement `MaterialSpecSeeder`**

Create `MaterialLedger.Web/Services/MaterialSpecSeeder.cs`:

```csharp
using MaterialLedger.Data.Materials;

namespace MaterialLedger.Web.Services;

public class MaterialSpecSeeder(MaterialSpecService materialSpecService)
{
    private sealed record SeedSpec(string Family, string? Grade, string? Finish, decimal ThicknessIn, string DisplayName);

    private sealed record GaugeEntry(string Label, decimal ThicknessIn);

    // Aluminum 5052 / 6061 share this gauge table in LaserQuote.
    private static readonly GaugeEntry[] AluminumGauges =
    [
        new("30ga", 0.0100m), new("28ga", 0.0126m), new("26ga", 0.0159m), new("24ga", 0.0201m),
        new("22ga", 0.0253m), new("20ga", 0.0320m), new("18ga", 0.0403m), new("16ga", 0.0508m),
        new("14ga", 0.0641m), new("13ga", 0.0720m), new("12ga", 0.0808m), new("11ga", 0.0910m),
        new("10ga", 0.1019m)
    ];

    // Steel A1008/CRS, A1011/HR, A1011/HRPO, and plain HRPO share this gauge table in LaserQuote.
    private static readonly GaugeEntry[] SteelGauges =
    [
        new("30ga", 0.0120m), new("28ga", 0.0149m), new("26ga", 0.0179m), new("24ga", 0.0239m),
        new("22ga", 0.0299m), new("20ga", 0.0359m), new("18ga", 0.0478m), new("16ga", 0.0598m),
        new("14ga", 0.0747m), new("13ga", 0.0897m), new("12ga", 0.1046m), new("11ga", 0.1196m),
        new("10ga", 0.1345m)
    ];

    private static readonly GaugeEntry[] StainlessGauges =
    [
        new("30ga", 0.0125m), new("28ga", 0.0156m), new("26ga", 0.0187m), new("24ga", 0.0250m),
        new("22ga", 0.0313m), new("20ga", 0.0375m), new("18ga", 0.0500m), new("16ga", 0.0625m),
        new("14ga", 0.0781m), new("13ga", 0.0938m), new("12ga", 0.1094m), new("11ga", 0.1250m),
        new("10ga", 0.1406m)
    ];

    // Steel A36 and A572 GR50 share this plate-thickness table in LaserQuote.
    private static readonly GaugeEntry[] PlateThicknesses =
    [
        new("3/16", 0.1875m), new("3ga", 0.2391m), new("1/4", 0.2500m), new("5/16", 0.3125m),
        new("3/8", 0.3750m), new("1/2", 0.5000m), new("5/8", 0.6250m), new("3/4", 0.7500m),
        new("1", 1.0000m)
    ];

    private static IEnumerable<SeedSpec> ForMaterial(string family, string? grade, string? finish, GaugeEntry[] gauges) =>
        gauges.Select(g => new SeedSpec(
            family, grade, finish, g.ThicknessIn,
            $"{family} {grade}{(finish is null ? "" : " " + finish)} — {g.Label} ({g.ThicknessIn:0.0000}\")"));

    private static readonly SeedSpec[] SeedSpecs =
    [
        .. ForMaterial("Aluminum", "5052", null, AluminumGauges),
        .. ForMaterial("Aluminum", "6061", null, AluminumGauges),
        .. ForMaterial("Stainless Steel", "304", null, StainlessGauges),
        .. ForMaterial("Steel", "A1008", "CRS", SteelGauges),
        .. ForMaterial("Steel", "A1011", "HR", SteelGauges),
        .. ForMaterial("Steel", "A1011", "HRPO", SteelGauges),
        .. ForMaterial("Steel", "HRPO", null, SteelGauges),
        .. ForMaterial("Steel", "A36", null, PlateThicknesses),
        .. ForMaterial("Steel", "A572 GR50", null, PlateThicknesses),
        new SeedSpec("Steel", "Floor Plate", null, 0.1875m, "Steel Floor Plate — 3/16 (0.1875\")")
    ];

    public async Task SeedAsync()
    {
        foreach (var s in SeedSpecs)
            await materialSpecService.GetOrCreateAsync(s.Family, s.Grade, s.Finish, s.ThicknessIn, s.DisplayName);
    }
}
```

- [ ] **Step 4: Wire the seeder into `Program.cs`**

In `MaterialLedger.Web/Program.cs`, add the registration next to the existing seeders (near line 17):

```csharp
builder.Services.AddScoped<LocationSeeder>();
builder.Services.AddScoped<MaterialSpecSeeder>();
```

And invoke it in the startup scope block (near line 67-68), after `locationSeeder.SeedAsync()`:

```csharp
    var locationSeeder = scope.ServiceProvider.GetRequiredService<LocationSeeder>();
    await locationSeeder.SeedAsync();

    var materialSpecSeeder = scope.ServiceProvider.GetRequiredService<MaterialSpecSeeder>();
    await materialSpecSeeder.SeedAsync();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MaterialSpecSeederTests"`
Expected: all 7 tests PASS.

- [ ] **Step 6: Build the whole solution to confirm `Program.cs` still compiles**

Run: `dotnet build`
Expected: build succeeds with no errors.

- [ ] **Step 7: Commit**

```bash
git add MaterialLedger.Web/Services/MaterialSpecSeeder.cs MaterialLedger.Web/Program.cs MaterialLedger.Tests/MaterialSpecSeederTests.cs
git commit -m "feat: seed MaterialSpecs from LaserQuote's material list"
```

---

### Task 2: Full regression run

**Files:** none (verification only).

**Interfaces:** none — this task only runs the existing suite.

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test`
Expected: every test passes, including the pre-existing `MaterialSpecServiceTests`, `LocationSeederTests`, `ReceiveMaterialServiceTests`, and the new `MaterialSpecSeederTests` — no regressions from the new seeder or `Program.cs` change.

- [ ] **Step 2: If anything fails, fix and re-run**

Investigate the specific failure (do not skip or delete a failing test to make the suite green). Re-run `dotnet test` until the full suite passes.

- [ ] **Step 3: Confirm working tree is clean**

Run: `git status`
Expected: no uncommitted changes remain (Task 1's commit already captured everything; this task produces no new file changes unless a regression fix was needed, in which case commit that fix separately with a message describing the fix).
