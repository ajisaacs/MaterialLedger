using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using MaterialLedger.Data;
using MaterialLedger.Data.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MaterialLedger.Tests.Api;

public class ReadEndpointTests : IClassFixture<ReadEndpointTests.ApiFactory>
{
    private readonly ApiFactory factory;

    public ReadEndpointTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Read_routes_require_authentication()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/v1/materials");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Materials_route_returns_active_specs_in_service_order_and_excludes_internal_fields()
    {
        var suffix = Guid.NewGuid().ToString("N");
        await SeedAsync(db =>
        {
            db.MaterialSpecs.AddRange(
                new MaterialSpec { MaterialFamily = $"Zinc-{suffix}", Grade = "B", Finish = "Mill", ThicknessIn = .25m, DisplayName = "Zinc", IsActive = true },
                new MaterialSpec { MaterialFamily = $"Aluminum-{suffix}", Grade = "A", Finish = "Brushed", ThicknessIn = .5m, DisplayName = "Aluminum", IsActive = true },
                new MaterialSpec { MaterialFamily = $"Hidden-{suffix}", Grade = "A", ThicknessIn = .1m, DisplayName = "Hidden", IsActive = false });
        });
        using var client = CreateAuthenticatedClient();

        var response = await client.GetAsync($"/api/v1/materials?q={Uri.EscapeDataString(suffix)}");
        var specs = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, specs.GetArrayLength());
        Assert.Equal($"Aluminum-{suffix}", specs[0].GetProperty("materialFamily").GetString());
        Assert.Equal($"Zinc-{suffix}", specs[1].GetProperty("materialFamily").GetString());
        AssertNoSensitiveFields(specs);
    }

    [Fact]
    public async Task Inventory_search_binds_every_filter_and_returns_canonical_available_summary()
    {
        var seed = await SeedInventoryAsync();
        using var client = CreateAuthenticatedClient();

        var searchResponse = await client.GetAsync(
            $"/api/v1/inventory?materialFamily={Uri.EscapeDataString(seed.MaterialFamily)}&grade={Uri.EscapeDataString(seed.Grade)}&finish={Uri.EscapeDataString(seed.Finish)}&thicknessIn={seed.ThicknessIn}&itemKind=Drop&minUsableWidthIn={seed.MinUsableWidthIn}&minUsableLengthIn={seed.MinUsableLengthIn}&locationCode={Uri.EscapeDataString(seed.LocationCode)}&availableOnly=true");
        var items = await searchResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        var item = Assert.Single(items.EnumerateArray());
        Assert.Equal(seed.DropId, item.GetProperty("id").GetInt64());
        Assert.Equal(4m, item.GetProperty("availableQuantity").GetDecimal());
        Assert.Equal(seed.LocationCode, item.GetProperty("locationCode").GetString());
        Assert.False(item.GetProperty("isStale").GetBoolean());
        Assert.Equal(seed.MaterialSpecDisplayName, item.GetProperty("materialSpecDisplayName").GetString());
        AssertNoSensitiveFields(items);
    }

    [Fact]
    public async Task Inventory_search_defaults_available_only_to_true()
    {
        var seed = await SeedInventoryAsync();
        using var client = CreateAuthenticatedClient();

        var response = await client.GetAsync(
            $"/api/v1/inventory?materialFamily={Uri.EscapeDataString(seed.MaterialFamily)}&grade={Uri.EscapeDataString(seed.Grade)}&finish={Uri.EscapeDataString(seed.Finish)}&thicknessIn={seed.ThicknessIn}&itemKind=Drop&minUsableWidthIn={seed.MinUsableWidthIn}&minUsableLengthIn={seed.MinUsableLengthIn}&locationCode={Uri.EscapeDataString(seed.LocationCode)}");
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(items.EnumerateArray());
        Assert.Equal(seed.DropId, item.GetProperty("id").GetInt64());
        Assert.DoesNotContain(items.EnumerateArray(), item => item.GetProperty("id").GetInt64() == seed.UnavailableDropId);
        AssertNoSensitiveFields(items);

        var includeUnavailableResponse = await client.GetAsync(
            $"/api/v1/inventory?materialFamily={Uri.EscapeDataString(seed.MaterialFamily)}&grade={Uri.EscapeDataString(seed.Grade)}&finish={Uri.EscapeDataString(seed.Finish)}&thicknessIn={seed.ThicknessIn}&itemKind=Drop&minUsableWidthIn={seed.MinUsableWidthIn}&minUsableLengthIn={seed.MinUsableLengthIn}&locationCode={Uri.EscapeDataString(seed.LocationCode)}&availableOnly=false");
        var includingUnavailable = await includeUnavailableResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, includeUnavailableResponse.StatusCode);
        Assert.Contains(includingUnavailable.EnumerateArray(), item => item.GetProperty("id").GetInt64() == seed.UnavailableDropId);
    }

    [Theory]
    [InlineData("materialFamily")]
    [InlineData("grade")]
    [InlineData("finish")]
    [InlineData("thicknessIn")]
    [InlineData("locationCode")]
    public async Task Inventory_search_individual_equality_filters_exclude_their_near_miss(string filterName)
    {
        var seed = await SeedInventoryAsync();
        using var client = CreateAuthenticatedClient();

        var queryValue = filterName switch
        {
            "materialFamily" => Uri.EscapeDataString(seed.MaterialFamily),
            "grade" => Uri.EscapeDataString(seed.Grade),
            "finish" => Uri.EscapeDataString(seed.Finish),
            "thicknessIn" => seed.ThicknessIn.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "locationCode" => Uri.EscapeDataString(seed.LocationCode),
            _ => throw new ArgumentOutOfRangeException(nameof(filterName))
        };
        var expectedNearMissId = filterName switch
        {
            "materialFamily" => seed.WrongMaterialFamilyId,
            "grade" => seed.WrongGradeId,
            "finish" => seed.WrongFinishId,
            "thicknessIn" => seed.WrongThicknessId,
            "locationCode" => seed.WrongLocationId,
            _ => throw new ArgumentOutOfRangeException(nameof(filterName))
        };

        var response = await client.GetAsync($"/api/v1/inventory?{filterName}={queryValue}&availableOnly=true");
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = items.EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(seed.DropId, ids);
        Assert.DoesNotContain(expectedNearMissId, ids);
    }

    [Fact]
    public async Task Inventory_search_item_kind_filter_excludes_matching_full_sheet_bundle()
    {
        var seed = await SeedInventoryAsync();
        using var client = CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/v1/inventory?itemKind=Drop&availableOnly=true");
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = items.EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(seed.DropId, ids);
        Assert.DoesNotContain(seed.BundleId, ids);
    }

    [Fact]
    public async Task Inventory_search_minimum_usable_width_filter_excludes_drop_just_below_threshold()
    {
        var seed = await SeedInventoryAsync();
        using var client = CreateAuthenticatedClient();

        var response = await client.GetAsync($"/api/v1/inventory?minUsableWidthIn={seed.MinUsableWidthIn}&availableOnly=true");
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = items.EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(seed.DropId, ids);
        Assert.DoesNotContain(seed.WrongWidthId, ids);
    }

    [Fact]
    public async Task Inventory_search_minimum_usable_length_filter_excludes_drop_just_below_threshold()
    {
        var seed = await SeedInventoryAsync();
        using var client = CreateAuthenticatedClient();

        var response = await client.GetAsync($"/api/v1/inventory?minUsableLengthIn={seed.MinUsableLengthIn}&availableOnly=true");
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = items.EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(seed.DropId, ids);
        Assert.DoesNotContain(seed.WrongLengthId, ids);
    }

    [Fact]
    public async Task Inventory_detail_routes_use_canonical_summary_and_only_add_source_sheet_for_drops()
    {
        var seed = await SeedInventoryAsync();
        using var client = CreateAuthenticatedClient();

        var bundleResponse = await client.GetAsync($"/api/v1/inventory/{seed.BundleId}");
        var bundleDetail = await bundleResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, bundleResponse.StatusCode);
        Assert.False(bundleDetail.TryGetProperty("sourceSheet", out _));
        Assert.Equal(4m, bundleDetail.GetProperty("availableQuantity").GetDecimal());
        AssertNoSensitiveFields(bundleDetail);

        var dropResponse = await client.GetAsync($"/api/v1/inventory/{seed.DropId}");
        var dropDetail = await dropResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, dropResponse.StatusCode);
        Assert.Equal(seed.BundleId, dropDetail.GetProperty("sourceSheet").GetProperty("id").GetInt64());
        AssertNoSensitiveFields(dropDetail);

        var missing = await client.GetAsync("/api/v1/inventory/99999999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Reservation_route_returns_the_opennest_and_pep_nest_references()
    {
        var nestId = Guid.NewGuid();
        long openNestHoldId = 0, pepHoldId = 0;
        await SeedAsync(async (MaterialLedgerDbContext db) =>
        {
            var user = new AppUser { Username = $"holder-{Guid.NewGuid():N}", DisplayName = "Holder", PasswordHash = "hash" };
            var spec = new MaterialSpec { MaterialFamily = "Steel", DisplayName = "Steel", ThicknessIn = .25m, IsActive = true };
            var location = new InventoryLocation { LocationCode = $"R-{Guid.NewGuid():N}"[..20], DisplayName = "Rack", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
            var item = new InventoryItem
            {
                PublicItemCode = $"RES-{Guid.NewGuid():N}"[..20], MaterialSpec = spec, InventoryLocation = location,
                ItemKind = InventoryItemKind.FullSheetBundle, Status = InventoryItemStatus.Available, QuantityOnHand = 2,
                ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedByUser = user
            };
            var openNestHold = new MaterialReservation { InventoryItem = item, Status = ReservationStatus.Proposed, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedByUser = user, OpenNestNestId = nestId, NestName = "ON-NEST-7" };
            var pepHold = new MaterialReservation { InventoryItem = item, Status = ReservationStatus.Proposed, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedByUser = user, PepNestYear = 2026, PepNestName = "NEST-31" };
            db.AddRange(item, openNestHold, pepHold);
            await db.SaveChangesAsync();
            openNestHoldId = openNestHold.MaterialReservationId;
            pepHoldId = pepHold.MaterialReservationId;
        });
        using var client = CreateAuthenticatedClient();

        var openNest = await client.GetFromJsonAsync<JsonElement>($"/api/v1/reservations/{openNestHoldId}");
        var pep = await client.GetFromJsonAsync<JsonElement>($"/api/v1/reservations/{pepHoldId}");

        Assert.Equal(nestId, openNest.GetProperty("openNestNestId").GetGuid());
        Assert.Equal("ON-NEST-7", openNest.GetProperty("nestName").GetString());
        Assert.Equal(JsonValueKind.Null, openNest.GetProperty("pepNestYear").ValueKind);
        Assert.Equal(2026, pep.GetProperty("pepNestYear").GetInt32());
        Assert.Equal("NEST-31", pep.GetProperty("pepNestName").GetString());
        Assert.Equal(JsonValueKind.Null, pep.GetProperty("openNestNestId").ValueKind);
        Assert.Equal(JsonValueKind.Null, pep.GetProperty("nestName").ValueKind);
    }

    [Fact]
    public async Task Locations_route_returns_active_projection_in_sort_order_without_navigation_graphs()
    {
        var suffix = Guid.NewGuid().ToString("N");
        long parentId = 0;
        await SeedAsync(async (MaterialLedgerDbContext db) =>
        {
            var parent = new InventoryLocation { LocationCode = $"P-{suffix}", DisplayName = "Parent", LocationType = InventoryLocationType.Rack, StorageMode = InventoryStorageMode.RolloutShelf, SortOrder = 20, IsActive = true };
            db.InventoryLocations.Add(parent);
            await db.SaveChangesAsync();
            parentId = parent.InventoryLocationId;
            db.InventoryLocations.AddRange(
                new InventoryLocation { LocationCode = $"Z-{suffix}", DisplayName = "Z", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, ParentInventoryLocationId = parentId, SortOrder = 10, CapacityConfirmed = true, IsActive = true, Notes = "usable" },
                new InventoryLocation { LocationCode = $"A-{suffix}", DisplayName = "A", LocationType = InventoryLocationType.Bay, StorageMode = InventoryStorageMode.VerticalDropBay, SortOrder = 10, IsActive = true },
                new InventoryLocation { LocationCode = $"X-{suffix}", DisplayName = "inactive", LocationType = InventoryLocationType.Bay, StorageMode = InventoryStorageMode.Other, SortOrder = 1, IsActive = false });
            await db.SaveChangesAsync();
        });
        using var client = CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/v1/locations");
        var locations = await response.Content.ReadFromJsonAsync<JsonElement>();
        var matching = locations.EnumerateArray().Where(x => x.GetProperty("code").GetString()!.EndsWith(suffix, StringComparison.Ordinal)).ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([$"A-{suffix}", $"Z-{suffix}", $"P-{suffix}"], matching.Select(x => x.GetProperty("code").GetString()));
        var child = Assert.Single(matching.Where(x => x.GetProperty("code").GetString() == $"Z-{suffix}"));
        Assert.Equal(parentId, child.GetProperty("parentId").GetInt64());
        Assert.Equal($"P-{suffix}", child.GetProperty("parentCode").GetString());
        Assert.True(child.GetProperty("capacityConfirmed").GetBoolean());
        AssertNoSensitiveFields(locations);
    }

    private async Task<(long BundleId, long DropId, long UnavailableDropId, long WrongWidthId, long WrongLengthId, long WrongMaterialFamilyId, long WrongGradeId, long WrongFinishId, long WrongThicknessId, long WrongLocationId, string LocationCode, string MaterialFamily, string Grade, string Finish, decimal ThicknessIn, decimal MinUsableWidthIn, decimal MinUsableLengthIn, string MaterialSpecDisplayName)> SeedInventoryAsync()
    {
        long bundleId = 0, dropId = 0, unavailableDropId = 0, wrongWidthId = 0, wrongLengthId = 0, wrongMaterialFamilyId = 0, wrongGradeId = 0, wrongFinishId = 0, wrongThicknessId = 0, wrongLocationId = 0;
        var locationCode = $"LOC-{Guid.NewGuid():N}"[..20];
        const string materialFamily = "API Filter Steel";
        const string grade = "A36-Filter";
        const string finish = "Mill-Filter";
        const decimal thicknessIn = .25m;
        const decimal minUsableWidthIn = 10m;
        const decimal minUsableLengthIn = 20m;
        const string materialSpecDisplayName = "A36 1/4";
        await SeedAsync(async db =>
        {
            var user = new AppUser { Username = $"u-{Guid.NewGuid():N}"[..20], DisplayName = "Observer", Role = AppUserRole.Manager, IsActive = true, PasswordHash = "x" };
            var spec = new MaterialSpec { MaterialFamily = materialFamily, Grade = grade, Finish = finish, ThicknessIn = thicknessIn, DisplayName = materialSpecDisplayName, IsActive = true };
            var wrongMaterialFamilySpec = new MaterialSpec { MaterialFamily = "Near Miss Aluminum", Grade = grade, Finish = finish, ThicknessIn = thicknessIn, DisplayName = "A36 1/4", IsActive = true };
            var wrongGradeSpec = new MaterialSpec { MaterialFamily = materialFamily, Grade = "A572-Near-Miss", Finish = finish, ThicknessIn = thicknessIn, DisplayName = "A36 1/4", IsActive = true };
            var wrongFinishSpec = new MaterialSpec { MaterialFamily = materialFamily, Grade = grade, Finish = "Galvanized-Near-Miss", ThicknessIn = thicknessIn, DisplayName = "A36 1/4", IsActive = true };
            var wrongThicknessSpec = new MaterialSpec { MaterialFamily = materialFamily, Grade = grade, Finish = finish, ThicknessIn = .375m, DisplayName = "A36 1/4", IsActive = true };
            var location = new InventoryLocation { LocationCode = locationCode, DisplayName = "API Location", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
            var otherLocation = new InventoryLocation { LocationCode = $"OTHER-{Guid.NewGuid():N}"[..20], DisplayName = "Other", LocationType = InventoryLocationType.Shelf, StorageMode = InventoryStorageMode.RolloutShelf, IsActive = true };
            db.AddRange(user, spec, wrongMaterialFamilySpec, wrongGradeSpec, wrongFinishSpec, wrongThicknessSpec, location, otherLocation);
            await db.SaveChangesAsync();
            var bundle = new InventoryItem { PublicItemCode = $"B-{Guid.NewGuid():N}"[..20], MaterialSpecId = spec.MaterialSpecId, InventoryLocationId = location.InventoryLocationId, ItemKind = InventoryItemKind.FullSheetBundle, Status = InventoryItemStatus.Available, QuantityOnHand = 5, ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = user.AppUserId };
            var drop = new InventoryItem { PublicItemCode = $"D-{Guid.NewGuid():N}"[..20], MaterialSpecId = spec.MaterialSpecId, InventoryLocationId = location.InventoryLocationId, ItemKind = InventoryItemKind.Drop, Status = InventoryItemStatus.Available, QuantityOnHand = 5, UsableWidthIn = minUsableWidthIn, UsableLengthIn = minUsableLengthIn, ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = user.AppUserId };
            var unavailableDrop = new InventoryItem { PublicItemCode = $"U-{Guid.NewGuid():N}"[..20], MaterialSpecId = spec.MaterialSpecId, InventoryLocationId = location.InventoryLocationId, ItemKind = InventoryItemKind.Drop, Status = InventoryItemStatus.Quarantined, QuantityOnHand = 5, UsableWidthIn = minUsableWidthIn, UsableLengthIn = minUsableLengthIn, ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = user.AppUserId };
            var wrongWidth = new InventoryItem { PublicItemCode = $"W-{Guid.NewGuid():N}"[..20], MaterialSpecId = spec.MaterialSpecId, InventoryLocationId = location.InventoryLocationId, ItemKind = InventoryItemKind.Drop, Status = InventoryItemStatus.Available, QuantityOnHand = 1, UsableWidthIn = 9, UsableLengthIn = 20, ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = user.AppUserId };
            var wrongLength = new InventoryItem { PublicItemCode = $"L-{Guid.NewGuid():N}"[..20], MaterialSpecId = spec.MaterialSpecId, InventoryLocationId = location.InventoryLocationId, ItemKind = InventoryItemKind.Drop, Status = InventoryItemStatus.Available, QuantityOnHand = 1, UsableWidthIn = 10, UsableLengthIn = 19, ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = user.AppUserId };
            var wrongMaterialFamily = new InventoryItem { PublicItemCode = $"F-{Guid.NewGuid():N}"[..20], MaterialSpecId = wrongMaterialFamilySpec.MaterialSpecId, InventoryLocationId = location.InventoryLocationId, ItemKind = InventoryItemKind.Drop, Status = InventoryItemStatus.Available, QuantityOnHand = 5, UsableWidthIn = 10, UsableLengthIn = 20, ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = user.AppUserId };
            var wrongGrade = new InventoryItem { PublicItemCode = $"G-{Guid.NewGuid():N}"[..20], MaterialSpecId = wrongGradeSpec.MaterialSpecId, InventoryLocationId = location.InventoryLocationId, ItemKind = InventoryItemKind.Drop, Status = InventoryItemStatus.Available, QuantityOnHand = 5, UsableWidthIn = 10, UsableLengthIn = 20, ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = user.AppUserId };
            var wrongFinish = new InventoryItem { PublicItemCode = $"N-{Guid.NewGuid():N}"[..20], MaterialSpecId = wrongFinishSpec.MaterialSpecId, InventoryLocationId = location.InventoryLocationId, ItemKind = InventoryItemKind.Drop, Status = InventoryItemStatus.Available, QuantityOnHand = 5, UsableWidthIn = 10, UsableLengthIn = 20, ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = user.AppUserId };
            var wrongThickness = new InventoryItem { PublicItemCode = $"T-{Guid.NewGuid():N}"[..20], MaterialSpecId = wrongThicknessSpec.MaterialSpecId, InventoryLocationId = location.InventoryLocationId, ItemKind = InventoryItemKind.Drop, Status = InventoryItemStatus.Available, QuantityOnHand = 5, UsableWidthIn = 10, UsableLengthIn = 20, ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = user.AppUserId };
            var wrongLocation = new InventoryItem { PublicItemCode = $"O-{Guid.NewGuid():N}"[..20], MaterialSpecId = spec.MaterialSpecId, InventoryLocationId = otherLocation.InventoryLocationId, ItemKind = InventoryItemKind.Drop, Status = InventoryItemStatus.Available, QuantityOnHand = 5, UsableWidthIn = 10, UsableLengthIn = 20, ShapeClassification = ShapeClassification.Rectangular, LastObservedAtUtc = DateTime.UtcNow, LastObservedBy = user.AppUserId };
            db.AddRange(bundle, drop, unavailableDrop, wrongWidth, wrongLength, wrongMaterialFamily, wrongGrade, wrongFinish, wrongThickness, wrongLocation);
            await db.SaveChangesAsync();
            db.MaterialReservations.AddRange(
                new MaterialReservation { InventoryItemId = bundle.InventoryItemId, Status = ReservationStatus.Approved, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedBy = user.AppUserId },
                new MaterialReservation { InventoryItemId = drop.InventoryItemId, Status = ReservationStatus.Approved, ReservedQuantity = 1, CreatedAtUtc = DateTime.UtcNow, CreatedBy = user.AppUserId });
            db.MaterialTransactions.Add(new MaterialTransaction { OccurredAtUtc = DateTime.UtcNow, RecordedAtUtc = DateTime.UtcNow, RecordedBy = user.AppUserId, TransactionType = MaterialTransactionType.RemnantCreated, InventoryItemId = drop.InventoryItemId, RelatedInventoryItemId = bundle.InventoryItemId, QuantityDelta = 1, SourceSystem = MaterialTransactionSourceSystem.OperatorUI });
            await db.SaveChangesAsync();
            bundleId = bundle.InventoryItemId;
            dropId = drop.InventoryItemId;
            unavailableDropId = unavailableDrop.InventoryItemId;
            wrongWidthId = wrongWidth.InventoryItemId;
            wrongLengthId = wrongLength.InventoryItemId;
            wrongMaterialFamilyId = wrongMaterialFamily.InventoryItemId;
            wrongGradeId = wrongGrade.InventoryItemId;
            wrongFinishId = wrongFinish.InventoryItemId;
            wrongThicknessId = wrongThickness.InventoryItemId;
            wrongLocationId = wrongLocation.InventoryItemId;
        });
        return (bundleId, dropId, unavailableDropId, wrongWidthId, wrongLengthId, wrongMaterialFamilyId, wrongGradeId, wrongFinishId, wrongThicknessId, wrongLocationId, locationCode, materialFamily, grade, finish, thicknessIn, minUsableWidthIn, minUsableLengthIn, materialSpecDisplayName);
    }

    private static void AssertNoSensitiveFields(JsonElement body)
    {
        var prohibited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "passwordHash", "normalizedKey", "rowVersion", "storagePath", "attachmentPath",
            "materialSpec", "inventoryLocation", "parentInventoryLocation", "sheetLot", "lastObservedByUser"
        };

        AssertNoProhibitedProperties(body, prohibited);
    }

    private static void AssertNoProhibitedProperties(JsonElement element, ISet<string> prohibited)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AssertNoProhibitedProperties(item, prohibited);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Assert.DoesNotContain(property.Name, prohibited);
                    AssertNoProhibitedProperties(property.Value, prohibited);
                }
                break;
        }
    }

    private async Task SeedAsync(Func<MaterialLedgerDbContext, Task> seed)
    {
        using var scope = factory.Services.CreateScope();
        await seed(scope.ServiceProvider.GetRequiredService<MaterialLedgerDbContext>());
    }

    private Task SeedAsync(Action<MaterialLedgerDbContext> seed) => SeedAsync(db => { seed(db); return db.SaveChangesAsync(); });

    private HttpClient CreateAuthenticatedClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RoleHeader, "Manager");
        return client;
    }

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private static readonly string DatabaseName = $"read-endpoint-tests-{Guid.NewGuid()}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MaterialLedgerDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<MaterialLedgerDbContext>>();
                services.RemoveAll<MaterialLedgerDbContext>();
                services.AddDbContext<MaterialLedgerDbContext>(options => options.UseInMemoryDatabase(DatabaseName));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.TestScheme;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.TestScheme;
                    options.DefaultForbidScheme = TestAuthenticationHandler.TestScheme;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.TestScheme, _ => { });
            });
        }
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "Test";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers[RoleHeader].ToString();
            if (string.IsNullOrWhiteSpace(role)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-user"), new Claim(ClaimTypes.Role, role)], TestScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
