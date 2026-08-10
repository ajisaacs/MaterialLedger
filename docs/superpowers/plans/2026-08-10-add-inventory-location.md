# Add Inventory Location Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a Programmer or Manager create new inventory locations (Areas and Racks) from the web UI, instead of editing the hardcoded `LocationSeeder`.

**Architecture:** A new idempotent `LocationService.CreateLocationAsync` (mirroring the existing `LocationSlotService.ConfirmLocationSlotsAsync` pattern: `OperationLogGuard` + serializable transaction) does validation and persistence. A new thin `Locations/Create` Razor Page binds the form, calls the service, and redirects on success. An "Add location" link is added to `Locations/Index.cshtml`, visible only to Programmer/Manager roles.

**Tech Stack:** ASP.NET Core Razor Pages, EF Core (SQL Server), xUnit integration tests against a real SQL Server test database (`SqlServerTestDatabaseFixture`).

## Global Constraints

- Location codes are normalized to uppercase, trimmed, and restricted to characters `A-Z`, `0-9`, `-` (spec: "Auto-uppercase + validate charset").
- Sort order is always auto-computed as `(max SortOrder among existing siblings sharing the same ParentInventoryLocationId, or -1 if none) + 1` — never a user-supplied field.
- Only `Area` and `Rack` are creatable through this feature. Slot-level types (`Shelf`, `Bay`, `Staging`) remain exclusive to the existing `ConfirmSlots` flow.
- Access is gated by `MaterialLedgerPolicies.RequireProgrammerOrManager` (same as `ConfirmSlotsModel`).
- New locations are created with `CapacityConfirmed = false`, `IsActive = true` (matches `LocationSeeder.ToEntity`).
- For `Area`, `StorageMode` is always persisted as `InventoryStorageMode.Other` regardless of what's submitted.
- Spec doc: `docs/superpowers/specs/2026-08-10-add-inventory-location-design.md`.

---

### Task 1: `LocationService.CreateLocationAsync`

**Files:**
- Create: `MaterialLedger.Data/Locations/LocationService.cs`
- Test: `MaterialLedger.Tests/LocationServiceTests.cs`

**Interfaces:**
- Consumes: `MaterialLedgerDbContext` (from `MaterialLedger.Data`), `OperationLogGuard` (`MaterialLedger.Data.Idempotency`, constructor `OperationLogGuard(MaterialLedgerDbContext db)`, methods `Task<OperationLogOutcome> CheckAsync(Guid, string, string)` and `void Record(Guid, string, string, string, int)`), `OperationConflictException` (thrown by `CheckAsync` on payload mismatch), `InventoryLocation` / `InventoryLocationType` / `InventoryStorageMode` (`MaterialLedger.Data.Models`).
- Produces:
  ```csharp
  namespace MaterialLedger.Data.Locations;

  public record CreateLocationRequest(
      string LocationCode,
      string DisplayName,
      InventoryLocationType LocationType,
      InventoryStorageMode StorageMode,
      long? ParentInventoryLocationId);

  public class LocationService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
  {
      public async Task<InventoryLocation> CreateLocationAsync(
          Guid correlationId, CreateLocationRequest request, int executedBy);
  }
  ```
  `CreateLocationAsync` throws `InvalidOperationException` for all validation failures (empty/invalid code, duplicate code, empty display name, unsupported `LocationType`, bad parent rules) and `OperationConflictException` when the correlation id was already used with a different payload (via `operationLog.CheckAsync`). Task 2 (the Razor Page) calls this method and catches both exception types.

This task has no dependency on Task 2; Task 2 depends on this task's public surface.

- [ ] **Step 1: Write the failing tests**

Create `MaterialLedger.Tests/LocationServiceTests.cs`:

```csharp
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Locations;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public class LocationServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public LocationServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    private async Task<int> SeedUserAsync(MaterialLedgerDbContext db)
    {
        var user = new AppUser { Username = $"t_{Guid.NewGuid():N}", DisplayName = "T", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
        db.Add(user);
        await db.SaveChangesAsync();
        return user.AppUserId;
    }

    private async Task<InventoryLocation> SeedAreaAsync(MaterialLedgerDbContext db, int sortOrder = 0)
    {
        var area = new InventoryLocation
        {
            LocationCode = $"AREA-{Guid.NewGuid():N}"[..20], DisplayName = "Area", LocationType = InventoryLocationType.Area,
            StorageMode = InventoryStorageMode.Other, SortOrder = sortOrder, IsActive = true
        };
        db.Add(area);
        await db.SaveChangesAsync();
        return area;
    }

    [Fact]
    public async Task CreateLocationAsync_Area_NoParent_Succeeds()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        var request = new CreateLocationRequest("new-area", "New Area", InventoryLocationType.Area, InventoryStorageMode.Other, null);
        var result = await service.CreateLocationAsync(Guid.NewGuid(), request, userId);

        Assert.Equal("NEW-AREA", result.LocationCode);
        Assert.Null(result.ParentInventoryLocationId);
        Assert.False(result.CapacityConfirmed);
        Assert.True(result.IsActive);
    }

    [Fact]
    public async Task CreateLocationAsync_RackUnderExistingArea_PersistsStorageModeAndParent()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var area = await SeedAreaAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        var request = new CreateLocationRequest("new-rack", "New Rack", InventoryLocationType.Rack, InventoryStorageMode.VerticalDropBay, area.InventoryLocationId);
        var result = await service.CreateLocationAsync(Guid.NewGuid(), request, userId);

        Assert.Equal(area.InventoryLocationId, result.ParentInventoryLocationId);
        Assert.Equal(InventoryStorageMode.VerticalDropBay, result.StorageMode);
    }

    [Fact]
    public async Task CreateLocationAsync_LowercaseCode_IsNormalizedToUppercase()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        var result = await service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("  mixed-Case  ", "Name", InventoryLocationType.Area, InventoryStorageMode.Other, null), userId);

        Assert.Equal("MIXED-CASE", result.LocationCode);
    }

    [Fact]
    public async Task CreateLocationAsync_CodeWithInvalidCharacters_Throws()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("BAD CODE!", "Name", InventoryLocationType.Area, InventoryStorageMode.Other, null), userId));
    }

    [Fact]
    public async Task CreateLocationAsync_DuplicateCode_Throws()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));
        await service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("DUP-CODE", "Name", InventoryLocationType.Area, InventoryStorageMode.Other, null), userId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("dup-code", "Name Again", InventoryLocationType.Area, InventoryStorageMode.Other, null), userId));
    }

    [Fact]
    public async Task CreateLocationAsync_RackWithNoParent_Throws()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("ORPHAN-RACK", "Name", InventoryLocationType.Rack, InventoryStorageMode.Other, null), userId));
    }

    [Fact]
    public async Task CreateLocationAsync_RackWithNonAreaParent_Throws()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var area = await SeedAreaAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));
        var rack = await service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("PARENT-RACK", "Rack", InventoryLocationType.Rack, InventoryStorageMode.Other, area.InventoryLocationId), userId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("CHILD-RACK", "Name", InventoryLocationType.Rack, InventoryStorageMode.Other, rack.InventoryLocationId), userId));
    }

    [Fact]
    public async Task CreateLocationAsync_AreaWithParentSpecified_Throws()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var area = await SeedAreaAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("CHILD-AREA", "Name", InventoryLocationType.Area, InventoryStorageMode.Other, area.InventoryLocationId), userId));
    }

    [Fact]
    public async Task CreateLocationAsync_SortOrder_IsMaxSiblingPlusOne()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var area = await SeedAreaAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));

        var firstRack = await service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("RACK-A", "Rack A", InventoryLocationType.Rack, InventoryStorageMode.Other, area.InventoryLocationId), userId);
        var secondRack = await service.CreateLocationAsync(Guid.NewGuid(),
            new CreateLocationRequest("RACK-B", "Rack B", InventoryLocationType.Rack, InventoryStorageMode.Other, area.InventoryLocationId), userId);

        Assert.Equal(0, firstRack.SortOrder);
        Assert.Equal(1, secondRack.SortOrder);
    }

    [Fact]
    public async Task CreateLocationAsync_RetryWithSameCorrelationId_ReturnsReplayWithoutDuplicating()
    {
        await using var db = _fixture.CreateContext();
        var userId = await SeedUserAsync(db);
        var service = new LocationService(db, new OperationLogGuard(db));
        var correlationId = Guid.NewGuid();
        var request = new CreateLocationRequest("REPLAY-AREA", "Name", InventoryLocationType.Area, InventoryStorageMode.Other, null);

        var first = await service.CreateLocationAsync(correlationId, request, userId);
        var second = await service.CreateLocationAsync(correlationId, request, userId);

        Assert.Equal(first.InventoryLocationId, second.InventoryLocationId);
        var count = await db.InventoryLocations.CountAsync(x => x.LocationCode == "REPLAY-AREA");
        Assert.Equal(1, count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile / fail**

Run: `dotnet test MaterialLedger.Tests --filter LocationServiceTests`
Expected: FAIL — `LocationService` and `CreateLocationRequest` do not exist yet (compile error).

- [ ] **Step 3: Implement `LocationService`**

Create `MaterialLedger.Data/Locations/LocationService.cs`:

```csharp
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data.Locations;

public record CreateLocationRequest(
    string LocationCode,
    string DisplayName,
    InventoryLocationType LocationType,
    InventoryStorageMode StorageMode,
    long? ParentInventoryLocationId);

public class LocationService(MaterialLedgerDbContext db, OperationLogGuard operationLog)
{
    public async Task<InventoryLocation> CreateLocationAsync(
        Guid correlationId, CreateLocationRequest request, int executedBy)
    {
        var normalizedCode = NormalizeCode(request.LocationCode);
        ValidateCode(normalizedCode);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
            throw new InvalidOperationException("Display name is required.");

        if (request.LocationType is not (InventoryLocationType.Area or InventoryLocationType.Rack))
            throw new InvalidOperationException($"Location type '{request.LocationType}' cannot be created directly — only Area and Rack are supported here.");

        var payloadHash = HashPayload(normalizedCode, request);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var outcome = await operationLog.CheckAsync(correlationId, "CreateLocation", payloadHash);
        if (outcome.Kind == OperationLogOutcomeKind.Replay)
        {
            var replayId = JsonSerializer.Deserialize<long>(outcome.ResultSummary!);
            var replay = await db.InventoryLocations.FirstAsync(x => x.InventoryLocationId == replayId);
            await transaction.CommitAsync();
            return replay;
        }

        if (await db.InventoryLocations.AnyAsync(x => x.LocationCode == normalizedCode))
            throw new InvalidOperationException($"Location code '{normalizedCode}' already exists.");

        InventoryLocation? parent = null;
        if (request.LocationType == InventoryLocationType.Area)
        {
            if (request.ParentInventoryLocationId is not null)
                throw new InvalidOperationException("An Area cannot have a parent location.");
        }
        else
        {
            if (request.ParentInventoryLocationId is null)
                throw new InvalidOperationException("A Rack must have a parent Area.");

            parent = await db.InventoryLocations.FirstOrDefaultAsync(x => x.InventoryLocationId == request.ParentInventoryLocationId)
                ?? throw new InvalidOperationException("Parent location does not exist.");

            if (parent.LocationType != InventoryLocationType.Area)
                throw new InvalidOperationException($"Parent location '{parent.LocationCode}' is a {parent.LocationType}, not an Area — Racks must be created under an Area.");
        }

        var maxSiblingSortOrder = await db.InventoryLocations
            .Where(x => x.ParentInventoryLocationId == request.ParentInventoryLocationId)
            .Select(x => (int?)x.SortOrder)
            .MaxAsync() ?? -1;

        var entity = new InventoryLocation
        {
            LocationCode = normalizedCode,
            DisplayName = request.DisplayName.Trim(),
            LocationType = request.LocationType,
            StorageMode = request.LocationType == InventoryLocationType.Area ? InventoryStorageMode.Other : request.StorageMode,
            SortOrder = maxSiblingSortOrder + 1,
            ParentInventoryLocationId = request.ParentInventoryLocationId,
            CapacityConfirmed = false,
            IsActive = true
        };
        db.InventoryLocations.Add(entity);
        await db.SaveChangesAsync();

        operationLog.Record(correlationId, "CreateLocation", payloadHash, JsonSerializer.Serialize(entity.InventoryLocationId), executedBy);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return entity;
    }

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();

    private static void ValidateCode(string normalizedCode)
    {
        if (normalizedCode.Length == 0)
            throw new InvalidOperationException("Location code is required.");
        if (!normalizedCode.All(c => (c is >= 'A' and <= 'Z') or (c is >= '0' and <= '9') or c == '-'))
            throw new InvalidOperationException("Location code may only contain letters, numbers, and hyphens.");
    }

    private static string HashPayload(string normalizedCode, CreateLocationRequest request)
    {
        var payload = JsonSerializer.Serialize(new
        {
            normalizedCode, request.DisplayName, request.LocationType, request.StorageMode, request.ParentInventoryLocationId
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test MaterialLedger.Tests --filter LocationServiceTests`
Expected: PASS (all 10 tests). Requires `MATERIALLEDGER_TEST_ADMIN_CONNECTION` to be set per `SqlServerTestDatabaseFixture` — use the same value already used for `LocationSlotServiceTests` in this environment.

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Data/Locations/LocationService.cs MaterialLedger.Tests/LocationServiceTests.cs
git commit -m "feat: add LocationService.CreateLocationAsync for creating Areas and Racks"
```

---

### Task 2: `Locations/Create` Razor Page

**Files:**
- Create: `MaterialLedger.Web/Pages/Locations/Create.cshtml`
- Create: `MaterialLedger.Web/Pages/Locations/Create.cshtml.cs`
- Modify: `MaterialLedger.Web/Program.cs:20` (register `LocationService` in DI, alongside the existing `LocationSlotService` registration)

**Interfaces:**
- Consumes: `LocationService.CreateLocationAsync(Guid, CreateLocationRequest, int)` and `CreateLocationRequest` from Task 1 (`MaterialLedger.Data.Locations`); `MaterialLedgerPolicies.RequireProgrammerOrManager` (`MaterialLedger.Web.Authorization`); `MaterialLedgerDbContext` (`MaterialLedger.Data`); `InventoryLocationType` / `InventoryStorageMode` / `InventoryLocation` (`MaterialLedger.Data.Models`); `OperationConflictException` (`MaterialLedger.Data.Idempotency`).
- Produces: page route `/Locations/Create` (GET renders the form, POST creates and redirects). No other task depends on this page's internals.

- [ ] **Step 1: Register `LocationService` in DI**

In `MaterialLedger.Web/Program.cs`, add a line immediately after the existing `LocationSlotService` registration (currently line 20):

```csharp
builder.Services.AddScoped<MaterialLedger.Data.Locations.LocationService>();
```

- [ ] **Step 2: Write `Create.cshtml.cs`**

Create `MaterialLedger.Web/Pages/Locations/Create.cshtml.cs`:

```csharp
using System.Security.Claims;
using MaterialLedger.Data;
using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Locations;
using MaterialLedger.Data.Models;
using MaterialLedger.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Web.Pages.Locations;

[Authorize(Policy = MaterialLedgerPolicies.RequireProgrammerOrManager)]
public class CreateModel(MaterialLedgerDbContext db, LocationService locationService) : PageModel
{
    [BindProperty] public string LocationCode { get; set; } = string.Empty;
    [BindProperty] public string DisplayName { get; set; } = string.Empty;
    [BindProperty] public InventoryLocationType LocationType { get; set; } = InventoryLocationType.Area;
    [BindProperty] public InventoryStorageMode StorageMode { get; set; } = InventoryStorageMode.Other;
    [BindProperty] public long? ParentInventoryLocationId { get; set; }
    [BindProperty] public Guid CorrelationId { get; set; }

    public List<InventoryLocation> AvailableAreas { get; set; } = [];
    public string? Error { get; set; }

    public async Task OnGetAsync()
    {
        AvailableAreas = await db.InventoryLocations
            .Where(x => x.LocationType == InventoryLocationType.Area && x.IsActive)
            .OrderBy(x => x.DisplayName)
            .ToListAsync();
        CorrelationId = Guid.NewGuid();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        AvailableAreas = await db.InventoryLocations
            .Where(x => x.LocationType == InventoryLocationType.Area && x.IsActive)
            .OrderBy(x => x.DisplayName)
            .ToListAsync();

        try
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var request = new CreateLocationRequest(LocationCode, DisplayName, LocationType, StorageMode, ParentInventoryLocationId);
            var created = await locationService.CreateLocationAsync(CorrelationId, request, userId);

            return created.LocationType == InventoryLocationType.Rack
                ? RedirectToPage("Details", new { code = created.LocationCode })
                : RedirectToPage("Index");
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationConflictException)
        {
            Error = ex is OperationConflictException
                ? "This form was already submitted with different values — please review and resubmit."
                : ex.Message;
            return Page();
        }
    }
}
```

- [ ] **Step 3: Write `Create.cshtml`**

Create `MaterialLedger.Web/Pages/Locations/Create.cshtml`:

```cshtml
@* MaterialLedger.Web/Pages/Locations/Create.cshtml *@
@page
@model MaterialLedger.Web.Pages.Locations.CreateModel
@{ Layout = "Shared/_Layout"; }
<header class="page-heading">
    <p class="eyebrow">Inventory setup</p>
    <h1>Add location</h1>
    <p>Create a new storage Area or Rack. Slots within a Rack are added afterward via Confirm Slots.</p>
</header>

@if (Model.Error is not null)
{
    <p style="color:red">@Model.Error</p>
}

<form method="post">
    @Html.AntiForgeryToken()
    <input type="hidden" asp-for="CorrelationId" />

    <fieldset>
        <legend>Location details</legend>
        <label>Location code <input asp-for="LocationCode" placeholder="e.g. L3-ROLL" autocomplete="off" /></label>
        <label>Display name <input asp-for="DisplayName" placeholder="e.g. Third Roll-Out Rack" autocomplete="off" /></label>
        <label>Location type
            <select asp-for="LocationType" id="location-type">
                <option value="@InventoryLocationType.Area">Area</option>
                <option value="@InventoryLocationType.Rack">Rack</option>
            </select>
        </label>
    </fieldset>

    <fieldset id="rack-fields">
        <legend>Rack details</legend>
        <label>Parent area
            <select asp-for="ParentInventoryLocationId" id="parent-area">
                <option value="">-- none --</option>
                @foreach (var area in Model.AvailableAreas)
                {
                    <option value="@area.InventoryLocationId">@area.DisplayName</option>
                }
            </select>
        </label>
        <label>Storage mode
            <select asp-for="StorageMode">
                <option value="@InventoryStorageMode.RolloutShelf">Roll-out shelf</option>
                <option value="@InventoryStorageMode.VerticalDropBay">Vertical drop bay</option>
                <option value="@InventoryStorageMode.FloorStaging">Floor staging</option>
                <option value="@InventoryStorageMode.Other">Other</option>
            </select>
        </label>
    </fieldset>

    <button type="submit">Add location</button>
</form>

<script>
    const locationType = document.getElementById("location-type");
    const rackFields = document.getElementById("rack-fields");

    function syncRackFieldsVisibility() {
        const isRack = locationType.value === "@InventoryLocationType.Rack";
        rackFields.style.display = isRack ? "" : "none";
    }

    locationType.addEventListener("change", syncRackFieldsVisibility);
    syncRackFieldsVisibility();
</script>
```

- [ ] **Step 4: Build the web project**

Run: `dotnet build MaterialLedger.Web`
Expected: build succeeds with no errors.

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/Locations/Create.cshtml MaterialLedger.Web/Pages/Locations/Create.cshtml.cs MaterialLedger.Web/Program.cs
git commit -m "feat: add Locations/Create page for adding Areas and Racks"
```

---

### Task 3: "Add location" navigation link

**Files:**
- Modify: `MaterialLedger.Web/Pages/Locations/Index.cshtml.cs`
- Modify: `MaterialLedger.Web/Pages/Locations/Index.cshtml`

**Interfaces:**
- Consumes: nothing new from Tasks 1–2 beyond the `/Locations/Create` route already produced by Task 2.
- Produces: `IndexModel.CanAddLocation` (bool) — a page-local property, not consumed elsewhere.

- [ ] **Step 1: Add `CanAddLocation` to `IndexModel`**

In `MaterialLedger.Web/Pages/Locations/Index.cshtml.cs`, add the property and set it in `OnGetAsync`:

```csharp
public bool CanAddLocation { get; set; }

public async Task OnGetAsync()
{
    Racks = await db.InventoryLocations
        .Where(x => x.LocationType == InventoryLocationType.Rack && x.IsActive)
        .OrderBy(x => x.SortOrder)
        .ToListAsync();
    Summaries = (await inventoryQuery.GetRackSummariesAsync()).ToDictionary(x => x.LocationCode);
    CanAddLocation = User.IsInRole("Programmer") || User.IsInRole("Manager");
}
```

- [ ] **Step 2: Add the link in `Index.cshtml`**

In `MaterialLedger.Web/Pages/Locations/Index.cshtml`, add a conditional link inside the `<header class="page-heading">` block, after the existing `<p>` description:

```cshtml
    @if (Model.CanAddLocation)
    {
        <p><a asp-page="Create">Add location →</a></p>
    }
```

- [ ] **Step 3: Build the web project**

Run: `dotnet build MaterialLedger.Web`
Expected: build succeeds with no errors.

- [ ] **Step 4: Manual verification**

Run the dev server (`dotnet run --project MaterialLedger.Web`) and, as a Programmer or Manager user, walk through:
1. `/Locations` shows the "Add location →" link; confirm it does NOT appear for an Operator-only user.
2. Click it, create an Area (leave parent unset) → redirected to `/Locations`.
3. Click "Add location →" again, create a Rack with the new Area as parent → redirected to `/Locations/Details/{code}`.
4. New Rack appears in the `/Locations` table with "Slots confirmed: No".
5. Submit the form again with the same location code → page re-renders with a "already exists" error, no duplicate row.
6. From the new Rack's Details page, use the existing "Confirm slots" flow to confirm it still works end-to-end.

- [ ] **Step 5: Commit**

```bash
git add MaterialLedger.Web/Pages/Locations/Index.cshtml MaterialLedger.Web/Pages/Locations/Index.cshtml.cs
git commit -m "feat: add navigation link to create inventory locations"
```
