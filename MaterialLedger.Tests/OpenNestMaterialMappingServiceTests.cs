using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Materials;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

public sealed class OpenNestMaterialKeyTests
{
    [Theory]
    [InlineData("  Mild  Steel ", "A36", 0.25, "mild steel|a36|0.25")]
    [InlineData("Stainless", "", 0.125, "stainless||0.125")]
    [InlineData("Stainless", "   ", 0.125, "stainless||0.125")]
    [InlineData("Stainless", null, 0.125, "stainless||0.125")]
    [InlineData("Aluminum", "5052\tH32", 0.1875, "aluminum|5052 h32|0.1875")]
    [InlineData("Aluminum", "6061", 1.0, "aluminum|6061|1")]
    public void For_normalizes_each_segment(string material, string? grade, double thickness, string expected)
    {
        Assert.Equal(expected, OpenNestMaterialKey.For(material, grade, (decimal)thickness));
    }

    [Fact]
    public void For_formats_thickness_with_the_invariant_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("steel|a36|0.1875", OpenNestMaterialKey.For("Steel", "A36", 0.1875m));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("", 0.25)]
    [InlineData("   ", 0.25)]
    [InlineData(null, 0.25)]
    [InlineData("Steel", 0)]
    [InlineData("Steel", -0.25)]
    public void For_rejects_blank_material_or_non_positive_thickness(string? material, double thickness)
    {
        Assert.Throws<InvalidOperationException>(() => OpenNestMaterialKey.For(material!, "A36", (decimal)thickness));
    }
}

[Trait("Category", "Integration")]
public sealed class OpenNestMaterialMappingServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public OpenNestMaterialMappingServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MapAsync_creates_a_single_catalog_mapping_and_replaces_the_selected_target()
    {
        await using var db = _fixture.CreateContext();
        var (user, first, replacement) = await SeedAsync(db, "on-remap");
        var service = new OpenNestMaterialMappingService(db, new OperationLogGuard(db));

        await service.MapAsync(Guid.NewGuid(), new MapOpenNestMaterialRequest("  Mild  Steel ", "A36", 0.25m, first.MaterialSpecId), user.AppUserId);
        var created = await db.MaterialSpecExternalMappings.AsNoTracking()
            .SingleAsync(x => x.ExternalSystem == "OpenNest" && x.ExternalKey == "mild steel|a36|0.25");
        Assert.Equal(first.MaterialSpecId, created.MaterialSpecId);

        await service.MapAsync(Guid.NewGuid(), new MapOpenNestMaterialRequest("Mild Steel", "a36", 0.250m, replacement.MaterialSpecId), user.AppUserId);

        var mapping = await db.MaterialSpecExternalMappings.SingleAsync(x => x.ExternalSystem == "OpenNest" && x.ExternalKey == "mild steel|a36|0.25");
        Assert.Equal(replacement.MaterialSpecId, mapping.MaterialSpecId);
        Assert.Equal(ExternalMappingStatus.Approved, mapping.Status);
        Assert.Equal(user.AppUserId, mapping.ApprovedBy);
        Assert.Equal(1, await db.MaterialSpecExternalMappings.CountAsync(x => x.ExternalSystem == "OpenNest" && x.ExternalKey == "mild steel|a36|0.25"));
    }

    [Fact]
    public async Task MapAsync_replays_a_repeated_correlation_id_without_applying_it_twice()
    {
        await using var db = _fixture.CreateContext();
        var (user, first, replacement) = await SeedAsync(db, "on-replay");
        var service = new OpenNestMaterialMappingService(db, new OperationLogGuard(db));
        var correlationId = Guid.NewGuid();
        var request = new MapOpenNestMaterialRequest("Replay Steel", "", 0.1875m, first.MaterialSpecId);

        await service.MapAsync(correlationId, request, user.AppUserId);
        await service.MapAsync(Guid.NewGuid(), new MapOpenNestMaterialRequest("Replay Steel", "", 0.1875m, replacement.MaterialSpecId), user.AppUserId);
        await service.MapAsync(correlationId, request, user.AppUserId);

        var mapping = await db.MaterialSpecExternalMappings.SingleAsync(x => x.ExternalSystem == "OpenNest" && x.ExternalKey == "replay steel||0.1875");
        Assert.Equal(replacement.MaterialSpecId, mapping.MaterialSpecId);
        Assert.Equal(1, await db.OperationLogs.CountAsync(x => x.CorrelationId == correlationId && x.ProcedureName == "MapOpenNestMaterial"));
    }

    [Fact]
    public async Task MapAsync_rejects_blank_material_non_positive_thickness_and_inactive_specs()
    {
        await using var db = _fixture.CreateContext();
        var (user, first, _) = await SeedAsync(db, "on-reject");
        var inactive = new MaterialSpec { MaterialFamily = "Steel", Grade = "Old", ThicknessIn = .25m, DisplayName = "Old 1/4", IsActive = false };
        db.Add(inactive);
        await db.SaveChangesAsync();
        var service = new OpenNestMaterialMappingService(db, new OperationLogGuard(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MapAsync(Guid.NewGuid(), new MapOpenNestMaterialRequest("  ", "A36", .25m, first.MaterialSpecId), user.AppUserId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MapAsync(Guid.NewGuid(), new MapOpenNestMaterialRequest("Reject Steel", "A36", 0m, first.MaterialSpecId), user.AppUserId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MapAsync(Guid.NewGuid(), new MapOpenNestMaterialRequest("Reject Steel", "A36", .25m, inactive.MaterialSpecId), user.AppUserId));

        Assert.False(await db.MaterialSpecExternalMappings.AnyAsync(x => x.ExternalSystem == "OpenNest" && x.ExternalKey.StartsWith("reject steel")));
    }

    private static async Task<(AppUser User, MaterialSpec First, MaterialSpec Replacement)> SeedAsync(Data.MaterialLedgerDbContext db, string prefix)
    {
        var user = new AppUser { Username = $"{prefix}-{Guid.NewGuid():N}", DisplayName = "OpenNest Mapper", PasswordHash = "hash", Role = AppUserRole.Manager };
        var first = new MaterialSpec { MaterialFamily = "Steel", Grade = $"HR-{Guid.NewGuid():N}", ThicknessIn = .1875m, DisplayName = "HR 3/16", IsActive = true };
        var replacement = new MaterialSpec { MaterialFamily = "Steel", Grade = $"A36-{Guid.NewGuid():N}", ThicknessIn = .1875m, DisplayName = "A36 3/16", IsActive = true };
        db.AddRange(user, first, replacement);
        await db.SaveChangesAsync();
        return (user, first, replacement);
    }
}
