# Material Ledger Phase 1 — Locations and Opening Census Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement Phase 1 of `REMCO-Laser-Material-Ledger-Design-Spec.md`: seed the ten rack-level locations plus the two floor skid-storage areas, build location/slot confirmation CRUD, and build the opening physical census workflow (append-only `OpeningBalance` transactions), so every subsequent phase has real ShopOps location and inventory data to work against.

**Architecture:** All new logic lives behind two new write-surface services in `MaterialLedger.Data` (`LocationSlotService.ConfirmLocationSlotsAsync`, `OpeningBalanceService.RecordOpeningBalanceAsync`), both guarded by the existing `OperationLogGuard` idempotency mechanism inside a `Serializable` DB transaction, matching the pattern already established in Phase 0. A `MaterialSpecService` supports look-up/creation of the reference `MaterialSpec` rows the census screen needs. Razor Pages under `Pages/Locations` and `Pages/Census` provide the touch-friendly UI, gated by the existing `RequireProgrammerOrManager` policy. No EF Core migration is needed — every table used here already exists from the Phase 0 `InitialCreate` migration.

**Tech Stack:** .NET 8, ASP.NET Core Razor Pages, EF Core 9 / SQL Server, xUnit (integration tests against a real SQL Server via `SqlServerTestDatabaseFixture`, following the existing Phase 0 test convention).

## Global Constraints

- All timestamps are UTC (`DateTime.UtcNow`), matching existing `datetime2` columns.
- Every new write procedure (`ConfirmLocationSlots`, `RecordOpeningBalance`) MUST go through `OperationLogGuard` inside a caller-owned `Serializable` transaction, per spec section 3 ("Every procedure checks this table first") — this is not optional per-procedure.
- `RecordOpeningBalance` and `ConfirmLocationSlots` are both Programmer/Manager-only per spec section 3's role table — enforce via `[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]` on every page that calls them. Never trust a client-supplied `AppUserId`; always resolve it from `User.FindFirst(ClaimTypes.NameIdentifier)` server-side (see `LoginModel` in Phase 0 for the claim shape).
- `InventoryItem.InventoryLocationId` must reference a leaf location (`Shelf`, `Bay`, `Staging`, `Quarantine`, `Scrap` — never `Area` or `Rack`), enforced in the service, not just the UI (spec section 3).
- Do not prepopulate quantities from photographs; the "copy from prior item" convenience must still force the operator to re-enter/confirm quantity (spec section 4A).
- An opening balance is the only way to establish availability — never derive it from PEP, LaserDashboard, purchase history, or the laser material library (spec section 1).
- Follow existing repo conventions exactly: primary-constructor DI (`public class Foo(Dependency dep)`), nullable reference types enabled, `[Trait("Category", "Integration")]` + `SqlServerTestDatabaseFixture` for any test touching real SQL Server behavior (sequences, computed columns, transactions), and per-task commits in the style of the existing git history (e.g. `feat: add opening balance census workflow with real-SQL-Server integration tests`).
- Grants for the live ShopOps `materialledger_app` login arrive with the code that uses them (see the header comment in `docs/schema/material-permissions-v1.sql`) — Task 7 adds a new grants script rather than editing the Phase 0 one. Applying it to the real `MaterialLedger` database on forge-sql is a manual, human-run administrative step (same as Phase 0) — do not attempt to run it against forge-sql automatically.

---

### Task 1: Location catalog seed + `LocationSeeder`

**Files:**
- Create: `MaterialLedger.Web/Services/LocationSeeder.cs`
- Modify: `MaterialLedger.Web/Program.cs`
- Test: `MaterialLedger.Tests/LocationSeederTests.cs`

**Interfaces:**
- Produces: `LocationSeeder.SeedAsync()` — idempotent, inserts `LASER-MATERIAL` plus the ten racks and two skid areas from spec section 2/3 if not already present.

- [ ] **Step 1: Write the failing test**

```csharp
// MaterialLedger.Tests/LocationSeederTests.cs
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class LocationSeederTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public LocationSeederTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SeedAsync_CreatesThirteenLocations_AreaPlusTenRacksPlusTwoSkids()
    {
        await using var db = _fixture.CreateContext();
        var seeder = new LocationSeeder(db);

        await seeder.SeedAsync();

        var count = await db.InventoryLocations.CountAsync();
        Assert.Equal(13, count);

        var area = await db.InventoryLocations.SingleAsync(x => x.LocationCode == "LASER-MATERIAL");
        Assert.Equal(InventoryLocationType.Area, area.LocationType);
        Assert.Null(area.ParentInventoryLocationId);

        var southRoll = await db.InventoryLocations.SingleAsync(x => x.LocationCode == "L1-SOUTH-ROLL");
        Assert.Equal(area.InventoryLocationId, southRoll.ParentInventoryLocationId);
        Assert.Equal(InventoryLocationType.Rack, southRoll.LocationType);
        Assert.Equal(InventoryStorageMode.RolloutShelf, southRoll.StorageMode);
        Assert.False(southRoll.CapacityConfirmed);

        var eastDrop = await db.InventoryLocations.SingleAsync(x => x.LocationCode == "EAST-DROP");
        Assert.Equal(InventoryStorageMode.VerticalDropBay, eastDrop.StorageMode);

        var skid = await db.InventoryLocations.SingleAsync(x => x.LocationCode == "SKID-EAST-WEST");
        Assert.Equal(InventoryStorageMode.FloorStaging, skid.StorageMode);
        Assert.Equal(area.InventoryLocationId, skid.ParentInventoryLocationId);
    }

    [Fact]
    public async Task SeedAsync_RunTwice_StaysIdempotent()
    {
        await using var db = _fixture.CreateContext();
        var seeder = new LocationSeeder(db);

        await seeder.SeedAsync();
        await seeder.SeedAsync();

        var count = await db.InventoryLocations.CountAsync();
        Assert.Equal(13, count);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter LocationSeederTests` (requires `MATERIALLEDGER_TEST_ADMIN_CONNECTION` set, per `SqlServerTestDatabaseFixture`)
Expected: FAIL — `LocationSeeder` does not exist yet (compile error).

- [ ] **Step 3: Implement `LocationSeeder`**

```csharp
// MaterialLedger.Web/Services/LocationSeeder.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Services;

public class LocationSeeder(MaterialLedgerDbContext db)
{
    private record SeedLocation(
        string Code, string DisplayName, InventoryLocationType Type,
        InventoryStorageMode Mode, int SortOrder, string? ParentCode);

    private static readonly SeedLocation[] SeedLocations =
    [
        new("LASER-MATERIAL", "Laser Material Storage Area", InventoryLocationType.Area, InventoryStorageMode.Other, 0, null),
        new("L1-SOUTH-ROLL", "Laser 1 South Roll-Out Rack", InventoryLocationType.Rack, InventoryStorageMode.RolloutShelf, 1, "LASER-MATERIAL"),
        new("L1-NORTH-ROLL", "Laser 1 North Roll-Out Rack", InventoryLocationType.Rack, InventoryStorageMode.RolloutShelf, 2, "LASER-MATERIAL"),
        new("L2-ROLL", "Laser 2 Roll-Out Rack", InventoryLocationType.Rack, InventoryStorageMode.RolloutShelf, 3, "LASER-MATERIAL"),
        new("MID-ROLL", "Mid-Floor Roll-Out Rack", InventoryLocationType.Rack, InventoryStorageMode.RolloutShelf, 4, "LASER-MATERIAL"),
        new("EAST-DROP", "East Drop Rack", InventoryLocationType.Rack, InventoryStorageMode.VerticalDropBay, 5, "LASER-MATERIAL"),
        new("MID-DROP", "Mid Drop Rack", InventoryLocationType.Rack, InventoryStorageMode.VerticalDropBay, 6, "LASER-MATERIAL"),
        new("STAINLESS-DROP", "Stainless Drop Rack", InventoryLocationType.Rack, InventoryStorageMode.VerticalDropBay, 7, "LASER-MATERIAL"),
        new("SE-DROP", "Southeast Drop Rack", InventoryLocationType.Rack, InventoryStorageMode.VerticalDropBay, 8, "LASER-MATERIAL"),
        new("EAST-TAN", "East Tan Rack", InventoryLocationType.Rack, InventoryStorageMode.Other, 9, "LASER-MATERIAL"),
        new("WEST-TAN", "West Tan Rack", InventoryLocationType.Rack, InventoryStorageMode.Other, 10, "LASER-MATERIAL"),
        new("SKID-EAST-WEST", "Skid Storage (between East Tan and West Tan)", InventoryLocationType.Rack, InventoryStorageMode.FloorStaging, 11, "LASER-MATERIAL"),
        new("SKID-EAST-STAINLESS", "Skid Storage (between East Tan and Stainless Drop)", InventoryLocationType.Rack, InventoryStorageMode.FloorStaging, 12, "LASER-MATERIAL"),
    ];

    public async Task SeedAsync()
    {
        var codeToId = await db.InventoryLocations.ToDictionaryAsync(x => x.LocationCode, x => x.InventoryLocationId);

        foreach (var seed in SeedLocations.Where(s => s.ParentCode is null))
        {
            if (codeToId.ContainsKey(seed.Code))
                continue;
            var entity = ToEntity(seed, parentId: null);
            db.InventoryLocations.Add(entity);
            await db.SaveChangesAsync();
            codeToId[seed.Code] = entity.InventoryLocationId;
        }

        foreach (var seed in SeedLocations.Where(s => s.ParentCode is not null))
        {
            if (codeToId.ContainsKey(seed.Code))
                continue;
            var entity = ToEntity(seed, parentId: codeToId[seed.ParentCode!]);
            db.InventoryLocations.Add(entity);
            await db.SaveChangesAsync();
            codeToId[seed.Code] = entity.InventoryLocationId;
        }
    }

    private static InventoryLocation ToEntity(SeedLocation seed, long? parentId) => new()
    {
        LocationCode = seed.Code,
        DisplayName = seed.DisplayName,
        LocationType = seed.Type,
        StorageMode = seed.Mode,
        SortOrder = seed.SortOrder,
        ParentInventoryLocationId = parentId,
        CapacityConfirmed = false,
        IsActive = true
    };
}
```

- [ ] **Step 4: Wire it into `Program.cs`**

```csharp
// MaterialLedger.Web/Program.cs — add alongside the existing AppUserSeeder/AppUserAuthenticator registrations
builder.Services.AddScoped<LocationSeeder>();
```

```csharp
// MaterialLedger.Web/Program.cs — inside the existing startup scope block, after the AppUserSeeder call
using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<AppUserSeeder>();
    await seeder.SeedAsync();

    var locationSeeder = scope.ServiceProvider.GetRequiredService<LocationSeeder>();
    await locationSeeder.SeedAsync();
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test --filter LocationSeederTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Web/Services/LocationSeeder.cs MaterialLedger.Web/Program.cs MaterialLedger.Tests/LocationSeederTests.cs
git commit -m "feat: seed the ten rack locations and two skid areas from the design spec"
```

---

### Task 2: `LocationSlotService` (`ConfirmLocationSlots` write procedure)

**Files:**
- Create: `MaterialLedger.Data/Locations/LocationSlotService.cs`
- Modify: `MaterialLedger.Web/Program.cs`
- Test: `MaterialLedger.Tests/LocationSlotServiceTests.cs`

**Interfaces:**
- Consumes: `MaterialLedgerDbContext` (existing), `OperationLogGuard` (existing, from `MaterialLedger.Data.Idempotency`).
- Produces: `SlotDefinition(string SlotCode, string DisplayName, int SortOrder)`, `LocationSlotResult { long ParentInventoryLocationId; IReadOnlyList<string> CreatedLocationCodes; }`, `LocationSlotConflictException`, `LocationSlotService.ConfirmLocationSlotsAsync(Guid correlationId, string parentLocationCode, IReadOnlyList<SlotDefinition> slots, int executedBy)` — used by Task 3's `ConfirmSlots` page.

- [ ] **Step 1: Write the failing test**

```csharp
// MaterialLedger.Tests/LocationSlotServiceTests.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Locations;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class LocationSlotServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public LocationSlotServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, string rackCode)> SeedRackAsync(MaterialLedgerDbContext db, InventoryStorageMode mode)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
        var rackCode = $"RACK-{Guid.NewGuid():N}"[..20];
        var rack = new InventoryLocation
        {
            LocationCode = rackCode, DisplayName = "Test Rack", LocationType = InventoryLocationType.Rack,
            StorageMode = mode, SortOrder = 1, CapacityConfirmed = false, IsActive = true
        };
        db.AddRange(user, rack);
        await db.SaveChangesAsync();
        return (user.AppUserId, rackCode);
    }

    [Fact]
    public async Task ConfirmLocationSlotsAsync_RolloutRack_CreatesShelvesAndMarksParentConfirmed()
    {
        await using var db = _fixture.CreateContext();
        var (userId, rackCode) = await SeedRackAsync(db, InventoryStorageMode.RolloutShelf);
        var service = new LocationSlotService(db, new OperationLogGuard(db));
        var slots = new[] { new SlotDefinition("S01", "Shelf 1", 1), new SlotDefinition("S02", "Shelf 2", 2) };

        var result = await service.ConfirmLocationSlotsAsync(Guid.NewGuid(), rackCode, slots, userId);

        Assert.Equal(2, result.CreatedLocationCodes.Count);
        var created = await db.InventoryLocations.Where(x => x.ParentInventoryLocationId == result.ParentInventoryLocationId).ToListAsync();
        Assert.All(created, x => Assert.Equal(InventoryLocationType.Shelf, x.LocationType));
        var parent = await db.InventoryLocations.FirstAsync(x => x.LocationCode == rackCode);
        Assert.True(parent.CapacityConfirmed);
    }

    [Fact]
    public async Task ConfirmLocationSlotsAsync_DropRack_CreatesBays()
    {
        await using var db = _fixture.CreateContext();
        var (userId, rackCode) = await SeedRackAsync(db, InventoryStorageMode.VerticalDropBay);
        var service = new LocationSlotService(db, new OperationLogGuard(db));

        var result = await service.ConfirmLocationSlotsAsync(Guid.NewGuid(), rackCode, [new SlotDefinition("B01", "Bay 1", 1)], userId);

        var created = await db.InventoryLocations.Where(x => x.ParentInventoryLocationId == result.ParentInventoryLocationId).ToListAsync();
        Assert.All(created, x => Assert.Equal(InventoryLocationType.Bay, x.LocationType));
    }

    [Fact]
    public async Task ConfirmLocationSlotsAsync_SkidRack_CreatesStagingPositions()
    {
        await using var db = _fixture.CreateContext();
        var (userId, rackCode) = await SeedRackAsync(db, InventoryStorageMode.FloorStaging);
        var service = new LocationSlotService(db, new OperationLogGuard(db));

        var result = await service.ConfirmLocationSlotsAsync(Guid.NewGuid(), rackCode, [new SlotDefinition("P01", "Position 1", 1)], userId);

        var created = await db.InventoryLocations.Where(x => x.ParentInventoryLocationId == result.ParentInventoryLocationId).ToListAsync();
        Assert.All(created, x => Assert.Equal(InventoryLocationType.Staging, x.LocationType));
    }

    [Fact]
    public async Task ConfirmLocationSlotsAsync_NonRackParent_Throws()
    {
        await using var db = _fixture.CreateContext();
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
        var area = new InventoryLocation
        {
            LocationCode = $"AREA-{Guid.NewGuid():N}"[..20], DisplayName = "Area", LocationType = InventoryLocationType.Area,
            StorageMode = InventoryStorageMode.Other, SortOrder = 0, IsActive = true
        };
        db.AddRange(user, area);
        await db.SaveChangesAsync();
        var service = new LocationSlotService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmLocationSlotsAsync(Guid.NewGuid(), area.LocationCode, [new SlotDefinition("S01", "Shelf 1", 1)], user.AppUserId));
    }

    [Fact]
    public async Task ConfirmLocationSlotsAsync_DuplicateSlotCode_ThrowsConflict()
    {
        await using var db = _fixture.CreateContext();
        var (userId, rackCode) = await SeedRackAsync(db, InventoryStorageMode.RolloutShelf);
        var service = new LocationSlotService(db, new OperationLogGuard(db));
        await service.ConfirmLocationSlotsAsync(Guid.NewGuid(), rackCode, [new SlotDefinition("S01", "Shelf 1", 1)], userId);

        await Assert.ThrowsAsync<LocationSlotConflictException>(() =>
            service.ConfirmLocationSlotsAsync(Guid.NewGuid(), rackCode, [new SlotDefinition("S01", "Shelf 1 Again", 1)], userId));
    }

    [Fact]
    public async Task ConfirmLocationSlotsAsync_RetryWithSameCorrelationId_ReturnsReplayWithoutDuplicating()
    {
        await using var db = _fixture.CreateContext();
        var (userId, rackCode) = await SeedRackAsync(db, InventoryStorageMode.RolloutShelf);
        var service = new LocationSlotService(db, new OperationLogGuard(db));
        var correlationId = Guid.NewGuid();
        var slots = new[] { new SlotDefinition("S01", "Shelf 1", 1) };

        var first = await service.ConfirmLocationSlotsAsync(correlationId, rackCode, slots, userId);
        var second = await service.ConfirmLocationSlotsAsync(correlationId, rackCode, slots, userId);

        Assert.Equal(first.CreatedLocationCodes, second.CreatedLocationCodes);
        var count = await db.InventoryLocations.CountAsync(x => x.ParentInventoryLocationId == first.ParentInventoryLocationId);
        Assert.Equal(1, count);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter LocationSlotServiceTests`
Expected: FAIL — `LocationSlotService` does not exist yet (compile error).

- [ ] **Step 3: Implement `LocationSlotService`**

```csharp
// MaterialLedger.Data/Locations/LocationSlotService.cs
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Locations;

public record SlotDefinition(string SlotCode, string DisplayName, int SortOrder);

public class LocationSlotResult
{
    public required long ParentInventoryLocationId { get; init; }
    public required IReadOnlyList<string> CreatedLocationCodes { get; init; }
}

public class LocationSlotConflictException(string message) : Exception(message);

public class LocationSlotService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    public async Task<LocationSlotResult> ConfirmLocationSlotsAsync(
        Guid correlationId, string parentLocationCode, IReadOnlyList<SlotDefinition> slots, int executedBy)
    {
        if (slots.Count == 0)
            throw new ArgumentException("At least one slot definition is required.", nameof(slots));

        var payloadHash = HashPayload(parentLocationCode, slots);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "ConfirmLocationSlots", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<LocationSlotResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var parent = await db.InventoryLocations.FirstOrDefaultAsync(x => x.LocationCode == parentLocationCode)
            ?? throw new InvalidOperationException($"Location '{parentLocationCode}' does not exist.");

        if (parent.LocationType != InventoryLocationType.Rack)
            throw new InvalidOperationException(
                $"Location '{parentLocationCode}' is a {parent.LocationType}, not a Rack — only racks can have slots confirmed.");

        var childType = ChildTypeFor(parent.StorageMode);
        var newCodes = slots.Select(s => $"{parentLocationCode}:{s.SlotCode}").ToList();

        var existingCodes = await db.InventoryLocations
            .Where(x => newCodes.Contains(x.LocationCode))
            .Select(x => x.LocationCode)
            .ToListAsync();
        if (existingCodes.Count > 0)
            throw new LocationSlotConflictException($"Slot code(s) already exist: {string.Join(", ", existingCodes)}");

        foreach (var slot in slots)
        {
            db.InventoryLocations.Add(new InventoryLocation
            {
                LocationCode = $"{parentLocationCode}:{slot.SlotCode}",
                DisplayName = slot.DisplayName,
                LocationType = childType,
                StorageMode = parent.StorageMode,
                SortOrder = slot.SortOrder,
                ParentInventoryLocationId = parent.InventoryLocationId,
                CapacityConfirmed = true,
                IsActive = true
            });
        }
        parent.CapacityConfirmed = true;

        var result = new LocationSlotResult { ParentInventoryLocationId = parent.InventoryLocationId, CreatedLocationCodes = newCodes };
        operationLog.Record(correlationId, "ConfirmLocationSlots", payloadHash, JsonSerializer.Serialize(result), executedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static InventoryLocationType ChildTypeFor(InventoryStorageMode parentMode) => parentMode switch
    {
        InventoryStorageMode.RolloutShelf => InventoryLocationType.Shelf,
        InventoryStorageMode.VerticalDropBay => InventoryLocationType.Bay,
        InventoryStorageMode.FloorStaging => InventoryLocationType.Staging,
        InventoryStorageMode.Other => InventoryLocationType.Shelf,
        _ => throw new ArgumentOutOfRangeException(nameof(parentMode))
    };

    private static string HashPayload(string parentLocationCode, IReadOnlyList<SlotDefinition> slots)
    {
        var payload = JsonSerializer.Serialize(new { parentLocationCode, slots });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
```

- [ ] **Step 4: Wire it into `Program.cs`**

```csharp
// MaterialLedger.Web/Program.cs
builder.Services.AddScoped<MaterialLedger.Data.Idempotency.OperationLogGuard>();
builder.Services.AddScoped<MaterialLedger.Data.Locations.LocationSlotService>();
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test --filter LocationSlotServiceTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Data/Locations/LocationSlotService.cs MaterialLedger.Web/Program.cs MaterialLedger.Tests/LocationSlotServiceTests.cs
git commit -m "feat: add ConfirmLocationSlots write procedure with idempotency and real-SQL-Server tests"
```

---

### Task 3: Locations pages (material map, rack detail, confirm slots, printable labels)

**Files:**
- Create: `MaterialLedger.Web/Pages/Locations/Index.cshtml`, `Index.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/Locations/Details.cshtml`, `Details.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/Locations/ConfirmSlots.cshtml`, `ConfirmSlots.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/Locations/Labels.cshtml`, `Labels.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Index.cshtml`

**Interfaces:**
- Consumes: `LocationSlotService.ConfirmLocationSlotsAsync` (Task 2), `MaterialLedgerDbContext` (existing), `MaterialLedgerPolicies.RequireProgrammerOrManager` (existing).

- [ ] **Step 1: Material map (`Locations/Index`)**

```csharp
// MaterialLedger.Web/Pages/Locations/Index.cshtml.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Locations;

[Authorize]
public class IndexModel(MaterialLedgerDbContext db) : PageModel
{
    public List<InventoryLocation> Racks { get; set; } = [];

    public async Task OnGetAsync()
    {
        Racks = await db.InventoryLocations
            .Where(x => x.LocationType == InventoryLocationType.Rack && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToListAsync();
    }
}
```

```html
@* MaterialLedger.Web/Pages/Locations/Index.cshtml *@
@page
@model MaterialLedger.Web.Pages.Locations.IndexModel
@{ Layout = "Shared/_Layout"; }
<h1>Material Map</h1>
<table>
    <thead><tr><th>Code</th><th>Rack</th><th>Storage</th><th>Slots confirmed</th></tr></thead>
    <tbody>
    @foreach (var rack in Model.Racks)
    {
        <tr>
            <td>@rack.LocationCode</td>
            <td><a asp-page="Details" asp-route-code="@rack.LocationCode">@rack.DisplayName</a></td>
            <td>@rack.StorageMode</td>
            <td>@(rack.CapacityConfirmed ? "Yes" : "No")</td>
        </tr>
    }
    </tbody>
</table>
```

- [ ] **Step 2: Rack detail (`Locations/Details`)**

```csharp
// MaterialLedger.Web/Pages/Locations/Details.cshtml.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Locations;

[Authorize]
public class DetailsModel(MaterialLedgerDbContext db) : PageModel
{
    public InventoryLocation? Rack { get; set; }
    public List<InventoryLocation> Slots { get; set; } = [];
    public bool CanConfirmSlots { get; set; }

    public async Task<IActionResult> OnGetAsync(string code)
    {
        Rack = await db.InventoryLocations.FirstOrDefaultAsync(x => x.LocationCode == code);
        if (Rack is null)
            return NotFound();

        Slots = await db.InventoryLocations
            .Where(x => x.ParentInventoryLocationId == Rack.InventoryLocationId && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToListAsync();
        CanConfirmSlots = !Rack.CapacityConfirmed && (User.IsInRole("Programmer") || User.IsInRole("Manager"));
        return Page();
    }
}
```

```html
@* MaterialLedger.Web/Pages/Locations/Details.cshtml *@
@page "{code}"
@model MaterialLedger.Web.Pages.Locations.DetailsModel
@{ Layout = "Shared/_Layout"; }
<h1>@Model.Rack!.DisplayName (@Model.Rack.LocationCode)</h1>
<p>Storage: @Model.Rack.StorageMode — Slots confirmed: @(Model.Rack.CapacityConfirmed ? "Yes" : "No")</p>
@if (Model.CanConfirmSlots)
{
    <p><a asp-page="ConfirmSlots" asp-route-code="@Model.Rack.LocationCode">Confirm slot count</a></p>
}
else if (Model.Rack.CapacityConfirmed)
{
    <p><a asp-page="Labels" asp-route-code="@Model.Rack.LocationCode">Printable labels</a></p>
}
<table>
    <thead><tr><th>Code</th><th>Name</th><th>Type</th></tr></thead>
    <tbody>
    @foreach (var slot in Model.Slots)
    {
        <tr>
            <td>@slot.LocationCode</td>
            <td>@slot.DisplayName</td>
            <td>@slot.LocationType</td>
        </tr>
    }
    </tbody>
</table>
<p><a asp-page="Index">Back to material map</a></p>
```

- [ ] **Step 3: Confirm slots (`Locations/ConfirmSlots`)**

```csharp
// MaterialLedger.Web/Pages/Locations/ConfirmSlots.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Locations;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Locations;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class ConfirmSlotsModel(MaterialLedgerDbContext db, LocationSlotService slotService) : PageModel
{
    [BindProperty] public string Code { get; set; } = string.Empty;
    [BindProperty] public int SlotCount { get; set; } = 1;
    [BindProperty] public int StartingNumber { get; set; } = 1;
    [BindProperty] public Guid CorrelationId { get; set; }

    public InventoryLocation? Rack { get; set; }
    public string SlotPrefix { get; set; } = "S";
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(string code)
    {
        Rack = await db.InventoryLocations.FirstOrDefaultAsync(x => x.LocationCode == code);
        if (Rack is null)
            return NotFound();
        Code = code;
        SlotPrefix = PrefixFor(Rack.StorageMode);
        CorrelationId = Guid.NewGuid();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Rack = await db.InventoryLocations.FirstOrDefaultAsync(x => x.LocationCode == Code);
        if (Rack is null)
            return NotFound();
        SlotPrefix = PrefixFor(Rack.StorageMode);

        if (SlotCount is < 1 or > 200)
        {
            Error = "Slot count must be between 1 and 200.";
            return Page();
        }

        var slots = Enumerable.Range(StartingNumber, SlotCount)
            .Select(n => new SlotDefinition($"{SlotPrefix}{n:D2}", $"{SlotPrefix}{n:D2}", n))
            .ToList();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await slotService.ConfirmLocationSlotsAsync(CorrelationId, Code, slots, userId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or LocationSlotConflictException)
        {
            Error = ex.Message;
            return Page();
        }

        return RedirectToPage("Details", new { code = Code });
    }

    private static string PrefixFor(InventoryStorageMode mode) => mode switch
    {
        InventoryStorageMode.RolloutShelf => "S",
        InventoryStorageMode.VerticalDropBay => "B",
        InventoryStorageMode.FloorStaging => "P",
        InventoryStorageMode.Other => "S",
        _ => "S"
    };
}
```

```html
@* MaterialLedger.Web/Pages/Locations/ConfirmSlots.cshtml *@
@page "{code}"
@model MaterialLedger.Web.Pages.Locations.ConfirmSlotsModel
@{ Layout = "Shared/_Layout"; }
<h1>Confirm slots for @Model.Rack?.DisplayName</h1>
@if (Model.Error is not null)
{
    <p style="color:red">@Model.Error</p>
}
<form method="post">
    @Html.AntiForgeryToken()
    <input type="hidden" asp-for="Code" />
    <input type="hidden" asp-for="CorrelationId" />
    <label>Number of slots (prefix "@Model.SlotPrefix") <input asp-for="SlotCount" type="number" min="1" max="200" /></label>
    <label>Starting number <input asp-for="StartingNumber" type="number" min="1" /></label>
    <button type="submit">Create slots</button>
</form>
```

- [ ] **Step 4: Printable labels (`Locations/Labels`)**

```csharp
// MaterialLedger.Web/Pages/Locations/Labels.cshtml.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Locations;

[Authorize]
public class LabelsModel(MaterialLedgerDbContext db) : PageModel
{
    public InventoryLocation? Rack { get; set; }
    public List<InventoryLocation> Slots { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(string code)
    {
        Rack = await db.InventoryLocations.FirstOrDefaultAsync(x => x.LocationCode == code);
        if (Rack is null)
            return NotFound();
        Slots = await db.InventoryLocations
            .Where(x => x.ParentInventoryLocationId == Rack.InventoryLocationId && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToListAsync();
        return Page();
    }
}
```

```html
@* MaterialLedger.Web/Pages/Locations/Labels.cshtml *@
@page "{code}"
@model MaterialLedger.Web.Pages.Locations.LabelsModel
@{ Layout = "Shared/_Layout"; }
<style>
  .label { border: 1px solid #000; padding: 1em; margin: 0.5em; display: inline-block; font-family: monospace; font-size: 1.2em; }
  @@media print { a, .no-print { display: none; } }
</style>
<h1 class="no-print">Printable labels — @Model.Rack?.DisplayName</h1>
<p class="no-print"><a href="javascript:window.print()">Print</a></p>
<div class="label">REMCO:ML:LOC:@Model.Rack?.LocationCode</div>
@foreach (var slot in Model.Slots)
{
    <div class="label">REMCO:ML:LOC:@slot.LocationCode</div>
}
```

- [ ] **Step 5: Add navigation from the home page**

```html
@* MaterialLedger.Web/Pages/Index.cshtml — replace the Phase 0 placeholder paragraph *@
@page
@model MaterialLedger.Web.Pages.IndexModel
@{ Layout = "Shared/_Layout"; }
<h1>Material Ledger</h1>
<ul>
    <li><a asp-page="/Locations/Index">Material map</a></li>
</ul>
```

- [ ] **Step 6: Build and manually verify**

Run: `dotnet build`
Expected: builds with no errors. Then, once a reachable dev SQL Server connection string is configured (see Task 8), run the app and confirm `/Locations/Index` lists all 13 seeded locations and `/Locations/Details/L1-SOUTH-ROLL` shows an empty slot list with a "Confirm slot count" link for a Programmer/Manager session.

- [ ] **Step 7: Commit**

```bash
git add MaterialLedger.Web/Pages/Locations MaterialLedger.Web/Pages/Index.cshtml
git commit -m "feat: add material map, rack detail, confirm-slots, and printable-label pages"
```

---

### Task 4: `MaterialSpecService` (reference-data lookup/creation)

**Files:**
- Create: `MaterialLedger.Data/Materials/MaterialSpecService.cs`
- Modify: `MaterialLedger.Web/Program.cs`
- Test: `MaterialLedger.Tests/MaterialSpecServiceTests.cs`

**Interfaces:**
- Produces: `MaterialSpecService.SearchAsync(string? query)`, `MaterialSpecService.GetOrCreateAsync(string materialFamily, string? grade, string? finish, decimal thicknessIn, string displayName)` — used by Task 6's census page.

- [ ] **Step 1: Write the failing test**

```csharp
// MaterialLedger.Tests/MaterialSpecServiceTests.cs
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter MaterialSpecServiceTests`
Expected: FAIL — `MaterialSpecService` does not exist yet (compile error).

- [ ] **Step 3: Implement `MaterialSpecService`**

```csharp
// MaterialLedger.Data/Materials/MaterialSpecService.cs
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Materials;

public class MaterialSpecService(MaterialLedgerDbContext db)
{
    public async Task<IReadOnlyList<MaterialSpec>> SearchAsync(string? query)
    {
        var specs = db.MaterialSpecs.Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            specs = specs.Where(x =>
                EF.Functions.Like(x.MaterialFamily, $"%{q}%") ||
                (x.Grade != null && EF.Functions.Like(x.Grade, $"%{q}%")) ||
                EF.Functions.Like(x.DisplayName, $"%{q}%"));
        }
        return await specs.OrderBy(x => x.MaterialFamily).ThenBy(x => x.Grade).ThenBy(x => x.ThicknessIn).ToListAsync();
    }

    public async Task<MaterialSpec> GetOrCreateAsync(string materialFamily, string? grade, string? finish, decimal thicknessIn, string displayName)
    {
        var existing = await db.MaterialSpecs.FirstOrDefaultAsync(x =>
            x.MaterialFamily == materialFamily && x.Grade == grade && x.Finish == finish && x.ThicknessIn == thicknessIn);
        if (existing is not null)
            return existing;

        var spec = new MaterialSpec
        {
            MaterialFamily = materialFamily,
            Grade = grade,
            Finish = finish,
            ThicknessIn = thicknessIn,
            DisplayName = displayName,
            IsActive = true
        };
        db.MaterialSpecs.Add(spec);
        try
        {
            await db.SaveChangesAsync();
            return spec;
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent insert of the same normalized key (unique index on
            // NormalizedKey) — the other insert won, so detach ours and return the one that landed.
            db.Entry(spec).State = EntityState.Detached;
            return await db.MaterialSpecs.FirstAsync(x =>
                x.MaterialFamily == materialFamily && x.Grade == grade && x.Finish == finish && x.ThicknessIn == thicknessIn);
        }
    }
}
```

- [ ] **Step 4: Wire it into `Program.cs`**

```csharp
// MaterialLedger.Web/Program.cs
builder.Services.AddScoped<MaterialLedger.Data.Materials.MaterialSpecService>();
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test --filter MaterialSpecServiceTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Data/Materials/MaterialSpecService.cs MaterialLedger.Web/Program.cs MaterialLedger.Tests/MaterialSpecServiceTests.cs
git commit -m "feat: add material spec lookup/creation service with real-SQL-Server tests"
```

---

### Task 5: `OpeningBalanceService` (`RecordOpeningBalance` write procedure)

**Files:**
- Create: `MaterialLedger.Data/Census/OpeningBalanceService.cs`
- Modify: `MaterialLedger.Web/Program.cs`
- Test: `MaterialLedger.Tests/OpeningBalanceServiceTests.cs`

**Interfaces:**
- Consumes: `MaterialLedgerDbContext`, `OperationLogGuard` (existing).
- Produces: `OpeningBalanceRequest(long InventoryLocationId, long MaterialSpecId, InventoryItemKind ItemKind, decimal Quantity, decimal? UsableWidthIn, decimal? UsableLengthIn, ShapeClassification ShapeClassification, bool IsQuarantined, string? Notes)`, `OpeningBalanceResult { long InventoryItemId; string PublicItemCode; }`, `OpeningBalanceService.RecordOpeningBalanceAsync(Guid correlationId, OpeningBalanceRequest request, int observedBy)`, `OpeningBalanceService.GetActiveItemsAtLocationAsync(long locationId)` — both used by Task 6's census page.

- [ ] **Step 1: Write the failing test**

```csharp
// MaterialLedger.Tests/OpeningBalanceServiceTests.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Census;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class OpeningBalanceServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public OpeningBalanceServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<(int userId, long locationId, long specId)> SeedAsync(
        MaterialLedgerDbContext db, InventoryLocationType locationType = InventoryLocationType.Shelf)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Programmer, IsActive = true, PasswordHash = "x" };
        var location = new InventoryLocation
        {
            LocationCode = $"LOC-{Guid.NewGuid():N}"[..30], DisplayName = "Slot", LocationType = locationType,
            StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true
        };
        var spec = new MaterialSpec { MaterialFamily = "Carbon Steel", Grade = "A36", ThicknessIn = 0.25m, DisplayName = "A36 1/4in", IsActive = true };
        db.AddRange(user, location, spec);
        await db.SaveChangesAsync();
        return (user.AppUserId, location.InventoryLocationId, spec.MaterialSpecId);
    }

    [Fact]
    public async Task RecordOpeningBalanceAsync_FullSheetBundle_CreatesItemAndOneImmutableTransaction()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var request = new OpeningBalanceRequest(locationId, specId, InventoryItemKind.FullSheetBundle, 6m, null, null, ShapeClassification.Rectangular, false, "Opening census");

        var result = await service.RecordOpeningBalanceAsync(Guid.NewGuid(), request, userId);

        var item = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == result.InventoryItemId);
        Assert.Equal(6m, item.QuantityOnHand);
        Assert.Equal(InventoryItemStatus.Available, item.Status);
        Assert.StartsWith("SHEET-", item.PublicItemCode);
        var transactions = await db.MaterialTransactions.Where(x => x.InventoryItemId == item.InventoryItemId).ToListAsync();
        Assert.Single(transactions);
        Assert.Equal(MaterialTransactionType.OpeningBalance, transactions[0].TransactionType);
        Assert.Equal(6m, transactions[0].QuantityDelta);
    }

    [Fact]
    public async Task RecordOpeningBalanceAsync_Drop_RequiresUsableGeometry()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db, InventoryLocationType.Bay);
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var request = new OpeningBalanceRequest(locationId, specId, InventoryItemKind.Drop, 1m, null, null, ShapeClassification.Rectangular, false, null);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordOpeningBalanceAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task RecordOpeningBalanceAsync_QuarantinedItem_IsNotAvailable()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var request = new OpeningBalanceRequest(locationId, specId, InventoryItemKind.FullSheetBundle, 3m, null, null, ShapeClassification.Unknown, true, "Unknown mixed stock");

        var result = await service.RecordOpeningBalanceAsync(Guid.NewGuid(), request, userId);

        var item = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == result.InventoryItemId);
        Assert.Equal(InventoryItemStatus.Quarantined, item.Status);
    }

    [Fact]
    public async Task RecordOpeningBalanceAsync_NonLeafLocation_Throws()
    {
        await using var db = _fixture.CreateContext();
        var (userId, _, specId) = await SeedAsync(db);
        var rack = new InventoryLocation
        {
            LocationCode = $"RACK-{Guid.NewGuid():N}"[..30], DisplayName = "Rack", LocationType = InventoryLocationType.Rack,
            StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true
        };
        db.Add(rack);
        await db.SaveChangesAsync();
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var request = new OpeningBalanceRequest(rack.InventoryLocationId, specId, InventoryItemKind.FullSheetBundle, 1m, null, null, ShapeClassification.Rectangular, false, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordOpeningBalanceAsync(Guid.NewGuid(), request, userId));
    }

    [Fact]
    public async Task RecordOpeningBalanceAsync_RetryWithSameCorrelationId_DoesNotDuplicate()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var request = new OpeningBalanceRequest(locationId, specId, InventoryItemKind.FullSheetBundle, 4m, null, null, ShapeClassification.Rectangular, false, null);
        var correlationId = Guid.NewGuid();

        var first = await service.RecordOpeningBalanceAsync(correlationId, request, userId);
        var second = await service.RecordOpeningBalanceAsync(correlationId, request, userId);

        Assert.Equal(first.PublicItemCode, second.PublicItemCode);
        var count = await db.InventoryItems.CountAsync(x => x.InventoryLocationId == locationId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetActiveItemsAtLocationAsync_ExcludesDepletedAndScrappedButIncludesAvailable()
    {
        await using var db = _fixture.CreateContext();
        var (userId, locationId, specId) = await SeedAsync(db);
        var service = new OpeningBalanceService(db, new OperationLogGuard(db));
        var depleted = await service.RecordOpeningBalanceAsync(Guid.NewGuid(),
            new OpeningBalanceRequest(locationId, specId, InventoryItemKind.FullSheetBundle, 2m, null, null, ShapeClassification.Rectangular, false, null), userId);
        var active = await service.RecordOpeningBalanceAsync(Guid.NewGuid(),
            new OpeningBalanceRequest(locationId, specId, InventoryItemKind.FullSheetBundle, 5m, null, null, ShapeClassification.Rectangular, false, null), userId);
        var depletedItem = await db.InventoryItems.SingleAsync(x => x.InventoryItemId == depleted.InventoryItemId);
        depletedItem.Status = InventoryItemStatus.Depleted;
        await db.SaveChangesAsync();

        var results = await service.GetActiveItemsAtLocationAsync(locationId);

        Assert.Single(results);
        Assert.Equal(active.InventoryItemId, results[0].InventoryItemId);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter OpeningBalanceServiceTests`
Expected: FAIL — `OpeningBalanceService` does not exist yet (compile error).

- [ ] **Step 3: Implement `OpeningBalanceService`**

```csharp
// MaterialLedger.Data/Census/OpeningBalanceService.cs
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Census;

public record OpeningBalanceRequest(
    long InventoryLocationId,
    long MaterialSpecId,
    InventoryItemKind ItemKind,
    decimal Quantity,
    decimal? UsableWidthIn,
    decimal? UsableLengthIn,
    ShapeClassification ShapeClassification,
    bool IsQuarantined,
    string? Notes);

public class OpeningBalanceResult
{
    public required long InventoryItemId { get; init; }
    public required string PublicItemCode { get; init; }
}

public class OpeningBalanceService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    private static readonly InventoryLocationType[] LeafLocationTypes =
    [
        InventoryLocationType.Shelf, InventoryLocationType.Bay, InventoryLocationType.Staging,
        InventoryLocationType.Quarantine, InventoryLocationType.Scrap
    ];

    public async Task<IReadOnlyList<InventoryItem>> GetActiveItemsAtLocationAsync(long locationId) =>
        await db.InventoryItems
            .Where(x => x.InventoryLocationId == locationId
                && x.Status != InventoryItemStatus.Depleted
                && x.Status != InventoryItemStatus.Scrapped)
            .Include(x => x.MaterialSpec)
            .ToListAsync();

    public async Task<OpeningBalanceResult> RecordOpeningBalanceAsync(Guid correlationId, OpeningBalanceRequest request, int observedBy)
    {
        Validate(request);

        var payloadHash = HashPayload(request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "RecordOpeningBalance", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replay = JsonSerializer.Deserialize<OpeningBalanceResult>(outcome.ResultSummary!)!;
            await transaction.CommitAsync();
            return replay;
        }

        var location = await db.InventoryLocations.FindAsync(request.InventoryLocationId)
            ?? throw new InvalidOperationException($"Location {request.InventoryLocationId} does not exist.");
        if (!LeafLocationTypes.Contains(location.LocationType))
            throw new InvalidOperationException(
                $"Location '{location.LocationCode}' is a {location.LocationType} — inventory can only be recorded at a leaf location.");

        var nextSeq = await db.Database.SqlQuery<long>($"SELECT NEXT VALUE FOR material.ItemCodeSeq").FirstAsync();
        var prefix = request.ItemKind == InventoryItemKind.Drop ? "DROP" : "SHEET";
        var publicItemCode = $"{prefix}-{nextSeq}";

        var observedAt = DateTime.UtcNow;
        var item = new InventoryItem
        {
            PublicItemCode = publicItemCode,
            MaterialSpecId = request.MaterialSpecId,
            InventoryLocationId = request.InventoryLocationId,
            ItemKind = request.ItemKind,
            Status = request.IsQuarantined ? InventoryItemStatus.Quarantined : InventoryItemStatus.Available,
            QuantityOnHand = request.Quantity,
            UsableWidthIn = request.UsableWidthIn,
            UsableLengthIn = request.UsableLengthIn,
            ShapeClassification = request.ShapeClassification,
            LastObservedAtUtc = observedAt,
            LastObservedBy = observedBy,
            Notes = request.Notes
        };
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        db.MaterialTransactions.Add(new MaterialTransaction
        {
            OccurredAtUtc = observedAt,
            RecordedAtUtc = observedAt,
            RecordedBy = observedBy,
            TransactionType = MaterialTransactionType.OpeningBalance,
            InventoryItemId = item.InventoryItemId,
            QuantityDelta = request.Quantity,
            GeometryWidthInSnapshot = request.UsableWidthIn,
            GeometryLengthInSnapshot = request.UsableLengthIn,
            Notes = request.Notes,
            SourceSystem = MaterialTransactionSourceSystem.OperatorUI
        });

        var result = new OpeningBalanceResult { InventoryItemId = item.InventoryItemId, PublicItemCode = publicItemCode };
        operationLog.Record(correlationId, "RecordOpeningBalance", payloadHash, JsonSerializer.Serialize(result), observedBy);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static void Validate(OpeningBalanceRequest request)
    {
        if (request.ItemKind == InventoryItemKind.Drop)
        {
            if (request.Quantity != 1)
                throw new ArgumentException("An opening balance for a drop must record exactly 1 unit.", nameof(request));
            if (request.UsableWidthIn is null or <= 0 || request.UsableLengthIn is null or <= 0)
                throw new ArgumentException("A drop requires a usable width and length greater than zero.", nameof(request));
        }
        else
        {
            if (request.Quantity < 1 || request.Quantity != decimal.Truncate(request.Quantity))
                throw new ArgumentException("Full sheets/bundles must be recorded in whole-sheet quantities of at least 1.", nameof(request));
        }
    }

    private static string HashPayload(OpeningBalanceRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
}
```

- [ ] **Step 4: Wire it into `Program.cs`**

```csharp
// MaterialLedger.Web/Program.cs
builder.Services.AddScoped<MaterialLedger.Data.Census.OpeningBalanceService>();
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test --filter OpeningBalanceServiceTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Data/Census/OpeningBalanceService.cs MaterialLedger.Web/Program.cs MaterialLedger.Tests/OpeningBalanceServiceTests.cs
git commit -m "feat: add RecordOpeningBalance write procedure with idempotency and real-SQL-Server tests"
```

---

### Task 6: Census pages (select slot, record opening balance, success)

**Files:**
- Create: `MaterialLedger.Web/Pages/Census/SelectSlot.cshtml`, `SelectSlot.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/Census/NewOpeningBalance.cshtml`, `NewOpeningBalance.cshtml.cs`
- Create: `MaterialLedger.Web/Pages/Census/Success.cshtml`, `Success.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Index.cshtml`

**Interfaces:**
- Consumes: `OpeningBalanceService` (Task 5), `MaterialSpecService` (Task 4), `MaterialLedgerPolicies.RequireProgrammerOrManager` (existing).

- [ ] **Step 1: Select slot page**

```csharp
// MaterialLedger.Web/Pages/Census/SelectSlot.cshtml.cs
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Census;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class SelectSlotModel(MaterialLedgerDbContext db) : PageModel
{
    public List<InventoryLocation> ConfirmedSlots { get; set; } = [];

    public async Task OnGetAsync()
    {
        ConfirmedSlots = await db.InventoryLocations
            .Where(x => x.IsActive && x.ParentInventoryLocationId != null
                && (x.LocationType == InventoryLocationType.Shelf
                    || x.LocationType == InventoryLocationType.Bay
                    || x.LocationType == InventoryLocationType.Staging))
            .Include(x => x.ParentInventoryLocation)
            .OrderBy(x => x.ParentInventoryLocation!.SortOrder).ThenBy(x => x.SortOrder)
            .ToListAsync();
    }
}
```

```html
@* MaterialLedger.Web/Pages/Census/SelectSlot.cshtml *@
@page
@model MaterialLedger.Web.Pages.Census.SelectSlotModel
@{ Layout = "Shared/_Layout"; }
<h1>Opening census — select a slot</h1>
<table>
    <thead><tr><th>Rack</th><th>Slot</th><th></th></tr></thead>
    <tbody>
    @foreach (var slot in Model.ConfirmedSlots)
    {
        <tr>
            <td>@slot.ParentInventoryLocation?.DisplayName</td>
            <td>@slot.LocationCode</td>
            <td><a asp-page="NewOpeningBalance" asp-route-locationId="@slot.InventoryLocationId">Record opening balance</a></td>
        </tr>
    }
    </tbody>
</table>
```

- [ ] **Step 2: New opening balance page**

```csharp
// MaterialLedger.Web/Pages/Census/NewOpeningBalance.cshtml.cs
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Census;
using MaterialLedger.Data.Materials;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Census;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class NewOpeningBalanceModel(MaterialLedgerDbContext db, OpeningBalanceService openingBalance, MaterialSpecService materialSpecs) : PageModel
{
    [BindProperty] public long LocationId { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }
    [BindProperty] public long? ExistingMaterialSpecId { get; set; }
    [BindProperty] public string MaterialFamily { get; set; } = string.Empty;
    [BindProperty] public string? Grade { get; set; }
    [BindProperty] public string? Finish { get; set; }
    [BindProperty] public decimal ThicknessIn { get; set; }
    [BindProperty] public InventoryItemKind ItemKind { get; set; } = InventoryItemKind.FullSheetBundle;
    [BindProperty] public decimal Quantity { get; set; }
    [BindProperty] public decimal? UsableWidthIn { get; set; }
    [BindProperty] public decimal? UsableLengthIn { get; set; }
    [BindProperty] public ShapeClassification ShapeClassification { get; set; } = ShapeClassification.Rectangular;
    [BindProperty] public bool IsQuarantined { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public InventoryLocation? Location { get; set; }
    public List<InventoryItem> ExistingActiveItems { get; set; } = [];
    public List<MaterialSpec> AvailableSpecs { get; set; } = [];
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(long locationId, long? copyFromItemId)
    {
        Location = await db.InventoryLocations.FindAsync(locationId);
        if (Location is null)
            return NotFound();

        LocationId = locationId;
        CorrelationId = Guid.NewGuid();
        ExistingActiveItems = (await openingBalance.GetActiveItemsAtLocationAsync(locationId)).ToList();
        AvailableSpecs = (await materialSpecs.SearchAsync(null)).ToList();

        if (copyFromItemId is not null)
        {
            var prior = ExistingActiveItems.FirstOrDefault(x => x.InventoryItemId == copyFromItemId);
            if (prior is not null)
            {
                ExistingMaterialSpecId = prior.MaterialSpecId;
                ItemKind = prior.ItemKind;
                UsableWidthIn = prior.UsableWidthIn;
                UsableLengthIn = prior.UsableLengthIn;
                ShapeClassification = prior.ShapeClassification;
                // Quantity is intentionally left at its default (0) — the operator must confirm a
                // real count, never inherit the prior item's quantity (spec section 4A).
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Location = await db.InventoryLocations.FindAsync(LocationId);
        ExistingActiveItems = (await openingBalance.GetActiveItemsAtLocationAsync(LocationId)).ToList();
        AvailableSpecs = (await materialSpecs.SearchAsync(null)).ToList();

        try
        {
            var specId = ExistingMaterialSpecId
                ?? (await materialSpecs.GetOrCreateAsync(MaterialFamily, Grade, Finish, ThicknessIn,
                    $"{MaterialFamily} {Grade} {ThicknessIn}in".Trim())).MaterialSpecId;

            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new OpeningBalanceRequest(LocationId, specId, ItemKind, Quantity,
                UsableWidthIn, UsableLengthIn, ShapeClassification, IsQuarantined, Notes);

            var result = await openingBalance.RecordOpeningBalanceAsync(CorrelationId, request, userId);
            return RedirectToPage("Success", new { code = result.PublicItemCode });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Error = ex.Message;
            return Page();
        }
    }
}
```

```html
@* MaterialLedger.Web/Pages/Census/NewOpeningBalance.cshtml *@
@page
@model MaterialLedger.Web.Pages.Census.NewOpeningBalanceModel
@{ Layout = "Shared/_Layout"; }
<h1>Opening balance — @Model.Location?.LocationCode</h1>

@if (Model.ExistingActiveItems.Count > 0)
{
    <div style="border:1px solid #900; padding:0.5em; margin-bottom:1em;">
        <strong>Already recorded at this slot — confirm this is a NEW item before continuing:</strong>
        <ul>
        @foreach (var item in Model.ExistingActiveItems)
        {
            <li>
                @item.PublicItemCode — @item.MaterialSpec?.DisplayName — qty @item.QuantityOnHand — @item.Status
                (<a asp-page="NewOpeningBalance" asp-route-locationId="@Model.LocationId" asp-route-copyFromItemId="@item.InventoryItemId">copy as new item</a>)
            </li>
        }
        </ul>
    </div>
}

@if (Model.Error is not null)
{
    <p style="color:red">@Model.Error</p>
}

<form method="post">
    @Html.AntiForgeryToken()
    <input type="hidden" asp-for="LocationId" />
    <input type="hidden" asp-for="CorrelationId" />

    <fieldset>
        <legend>Material</legend>
        <label>Existing spec
            <select asp-for="ExistingMaterialSpecId">
                <option value="">-- new material --</option>
                @foreach (var spec in Model.AvailableSpecs)
                {
                    <option value="@spec.MaterialSpecId">@spec.DisplayName</option>
                }
            </select>
        </label>
        <p>Or enter a new material:</p>
        <label>Family <input asp-for="MaterialFamily" /></label>
        <label>Grade <input asp-for="Grade" /></label>
        <label>Finish <input asp-for="Finish" /></label>
        <label>Thickness (in) <input asp-for="ThicknessIn" type="number" step="0.0001" /></label>
    </fieldset>

    <fieldset>
        <legend>Item</legend>
        <label><input type="radio" asp-for="ItemKind" value="FullSheetBundle" /> Full sheet / bundle</label>
        <label><input type="radio" asp-for="ItemKind" value="FullSheet" /> Single full sheet</label>
        <label><input type="radio" asp-for="ItemKind" value="Drop" /> Drop</label>
        <label>Quantity <input asp-for="Quantity" type="number" step="1" /></label>
        <label>Usable width (in) <input asp-for="UsableWidthIn" type="number" step="0.001" /></label>
        <label>Usable length (in) <input asp-for="UsableLengthIn" type="number" step="0.001" /></label>
        <label>Shape
            <select asp-for="ShapeClassification">
                <option value="Rectangular">Rectangular</option>
                <option value="IrregularConservativeRectangle">Irregular (conservative rectangle)</option>
                <option value="Unknown">Unknown</option>
            </select>
        </label>
    </fieldset>

    <label><input type="checkbox" asp-for="IsQuarantined" /> Unknown / mixed — quarantine (not available for reservation)</label>
    <label>Notes <textarea asp-for="Notes"></textarea></label>

    <button type="submit">Save opening balance</button>
</form>
```

- [ ] **Step 3: Success page**

```csharp
// MaterialLedger.Web/Pages/Census/Success.cshtml.cs
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MaterialLedger.Web.Pages.Census;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class SuccessModel : PageModel
{
    [BindProperty(SupportsGet = true)] public string Code { get; set; } = string.Empty;
    public void OnGet() { }
}
```

```html
@* MaterialLedger.Web/Pages/Census/Success.cshtml *@
@page
@model MaterialLedger.Web.Pages.Census.SuccessModel
@{ Layout = "Shared/_Layout"; }
<h1>Opening balance recorded</h1>
<p>Item code: <strong>@Model.Code</strong></p>
<p>QR payload for a printed label (drops only): <code>@Model.Code</code></p>
<p><a asp-page="SelectSlot">Record another item</a></p>
```

- [ ] **Step 4: Add census navigation link**

```html
@* MaterialLedger.Web/Pages/Index.cshtml — extend the nav list from Task 3 *@
@page
@model MaterialLedger.Web.Pages.IndexModel
@{ Layout = "Shared/_Layout"; }
<h1>Material Ledger</h1>
<ul>
    <li><a asp-page="/Locations/Index">Material map</a></li>
    @if (User.IsInRole("Programmer") || User.IsInRole("Manager"))
    {
        <li><a asp-page="/Census/SelectSlot">Opening census</a></li>
    }
</ul>
```

- [ ] **Step 5: Build and manually verify**

Run: `dotnet build`
Expected: builds with no errors. Manual verification happens together with Task 3's in Task 8.

- [ ] **Step 6: Commit**

```bash
git add MaterialLedger.Web/Pages/Census MaterialLedger.Web/Pages/Index.cshtml
git commit -m "feat: add opening census workflow pages (select slot, record balance, success)"
```

---

### Task 7: Phase 1 ShopOps grants script

**Files:**
- Create: `docs/schema/material-permissions-v2.sql`

**Interfaces:**
- None (SQL script; no code dependency). Documents the additional least-privilege grants Phase 1's code paths require on the real `MaterialLedger` database.

- [ ] **Step 1: Write the grants script**

```sql
-- docs/schema/material-permissions-v2.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, Phase 1.
--
-- Phase 1 introduces the locations/census write surface: LocationSeeder (startup catalog seed),
-- LocationSlotService.ConfirmLocationSlotsAsync, OpeningBalanceService.RecordOpeningBalanceAsync,
-- and MaterialSpecService's reference-data lookup/creation. Per the note at the top of
-- material-permissions-v1.sql, grants arrive with the code that uses them — this file adds ONLY
-- what Phase 1 needs. It does not touch the Phase 0 grants already applied by
-- material-permissions-v1.sql, and is safe to re-run (GRANT is idempotent).
--
-- Run against the real `MaterialLedger` database using an account with GRANT rights (sa or a
-- member of db_owner/db_securityadmin), the same way material-permissions-v1.sql was applied. This
-- is a manual administrative step — the running application never applies its own grants.

USE MaterialLedger;
GO

-- InventoryLocations: LocationSeeder inserts the rack/skid catalog at startup; LocationSlotService
-- inserts child slots and updates the parent rack's CapacityConfirmed flag during census setup.
GRANT SELECT, INSERT, UPDATE ON material.InventoryLocations TO materialledger_app;

-- InventoryItems: OpeningBalanceService inserts one row per physically observed sheet/drop. No
-- UPDATE/DELETE yet — Phase 1 only ever creates opening-balance items; quantity/status mutation
-- (reservations, consumption) arrives with Phase 2/3's write surface.
GRANT SELECT, INSERT ON material.InventoryItems TO materialledger_app;

-- MaterialSpecs: MaterialSpecService looks up or creates reference rows for the material picked
-- during census. Reference data, not the immutable ledger itself — ordinary UPDATE is intentionally
-- withheld for now since Phase 1 has no edit-existing-spec workflow.
GRANT SELECT, INSERT ON material.MaterialSpecs TO materialledger_app;

-- ItemCodeSeq: OpeningBalanceService calls NEXT VALUE FOR to mint each InventoryItem's
-- PublicItemCode. SQL Server requires UPDATE permission on a sequence object to consume a value
-- from it (NEXT VALUE FOR mutates the sequence's internal counter).
GRANT UPDATE ON material.ItemCodeSeq TO materialledger_app;
GO
```

- [ ] **Step 2: Commit**

```bash
git add docs/schema/material-permissions-v2.sql
git commit -m "docs: add Phase 1 ShopOps grants for locations, items, specs, and the item-code sequence"
```

- [ ] **Step 3: Flag the manual administrative follow-up**

This script must be applied to the real ShopOps `MaterialLedger` database on forge-sql by whoever holds the approved SQL-administration path (same person/process that applied `material-permissions-v1.sql` in Phase 0) before the Phase 1 UI is enabled against production. Do not run this automatically — surface it as an explicit follow-up to the user.

---

### Task 8: Manual verification pass

**Files:** none (verification only).

- [ ] **Step 1: Run the full automated suite**

Run: `dotnet test` (with `MATERIALLEDGER_TEST_ADMIN_CONNECTION` set to an sa-level connection string for a reachable SQL Server, e.g. forge-sql at `10.10.100.134`)
Expected: all tests pass, including the five new integration test classes from Tasks 1, 2, 4, and 5.

- [ ] **Step 2: Point the app at a dev database and run it**

Set `ConnectionStrings:MaterialLedger` (via user-secrets, since `MaterialLedger.Web` already has `UserSecretsId` configured — see `chore: enable user-secrets for MaterialLedger.Web` in git history) to a **dev** database — do not point the running app at production `MaterialLedger` or the test suite's throwaway `MaterialLedgerTest`. Confirm with the user which database to use before creating one on shared infrastructure (forge-sql), per this session's standing rule on shared-infrastructure changes. Then:

Run: `dotnet run --project MaterialLedger.Web`

- [ ] **Step 3: Walk the workflow in a browser** (use the `webapp-testing` skill / Playwright tools)

1. Sign in as the seeded Programmer or Manager account.
2. Open `/Locations/Index` — confirm all 13 seeded locations appear (`LASER-MATERIAL` won't be listed since only `Rack`-type rows show; confirm the ten racks + two skid areas are present).
3. Open `/Locations/Details/L1-SOUTH-ROLL`, click "Confirm slot count", create 4 shelves, confirm the redirect back to Details shows `S01`–`S04`.
4. Open `/Locations/Labels/L1-SOUTH-ROLL`, confirm the rack code and all four shelf codes render as `REMCO:ML:LOC:...` text.
5. Open `/Census/SelectSlot`, pick `L1-SOUTH-ROLL:S01`, record an opening balance (full sheet bundle, quantity 5), confirm the Success page shows a `SHEET-` code.
6. Return to `/Census/SelectSlot`, pick the same slot again, confirm the "Already recorded at this slot" warning lists the item just created, and that "copy as new item" pre-fills material/geometry but leaves quantity blank.
7. Sign in as the seeded Operator account, confirm `/Census/SelectSlot` and `/Locations/ConfirmSlots/...` both return a 403 (Operator is not Programmer/Manager).

- [ ] **Step 4: Report results to the user**

Summarize pass/fail for each step above; do not claim Phase 1 complete until this walkthrough has actually been run and confirmed, per this project's standing rule to verify UI changes in a real browser before reporting success.
