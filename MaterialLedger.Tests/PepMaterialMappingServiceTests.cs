using MaterialLedger.Data.Idempotency;
using MaterialLedger.Data.Materials;
using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

[Trait("Category", "Integration")]
public sealed class PepMaterialMappingServiceTests : IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;
    public PepMaterialMappingServiceTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MapAsync_approved_PEP_material_creates_a_single_catalog_mapping_and_replaces_the_selected_target()
    {
        await using var db = _fixture.CreateContext();
        var user = new AppUser { Username = "pep-map-user", DisplayName = "PEP Mapper", PasswordHash = "hash", Role = AppUserRole.Manager };
        var first = new MaterialSpec { MaterialFamily = "Steel", Grade = "HR", ThicknessIn = .1875m, DisplayName = "HR 3/16", IsActive = true };
        var replacement = new MaterialSpec { MaterialFamily = "Steel", Grade = "A36", ThicknessIn = .1875m, DisplayName = "A36 3/16", IsActive = true };
        db.AddRange(user, first, replacement);
        await db.SaveChangesAsync();
        var service = new PepMaterialMappingService(db, new OperationLogGuard(db));

        await service.MapAsync(Guid.NewGuid(), new MapPepMaterialRequest(1019, first.MaterialSpecId), user.AppUserId);
        await service.MapAsync(Guid.NewGuid(), new MapPepMaterialRequest(1019, replacement.MaterialSpecId), user.AppUserId);

        var mapping = await db.MaterialSpecExternalMappings.SingleAsync(x => x.ExternalSystem == "PEP" && x.ExternalKey == "1019");
        Assert.Equal(replacement.MaterialSpecId, mapping.MaterialSpecId);
        Assert.Equal(ExternalMappingStatus.Approved, mapping.Status);
        Assert.Equal(user.AppUserId, mapping.ApprovedBy);
        Assert.Equal(1, await db.MaterialSpecExternalMappings.CountAsync(x => x.ExternalSystem == "PEP" && x.ExternalKey == "1019"));
    }
}
