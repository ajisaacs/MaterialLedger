using MaterialLedger.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MaterialLedger.Data;

public class MaterialLedgerDbContext(DbContextOptions<MaterialLedgerDbContext> options)
    : DbContext(options)
{
    public const string Schema = "material";

    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<MaterialSpec> MaterialSpecs => Set<MaterialSpec>();
    public DbSet<MaterialSpecExternalMapping> MaterialSpecExternalMappings => Set<MaterialSpecExternalMapping>();
    public DbSet<InventoryLocation> InventoryLocations => Set<InventoryLocation>();
    public DbSet<SheetLot> SheetLots => Set<SheetLot>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<PurchaseOrderLineAllocation> PurchaseOrderLineAllocations => Set<PurchaseOrderLineAllocation>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<MaterialReservation> MaterialReservations => Set<MaterialReservation>();
    public DbSet<ExpectedRemnant> ExpectedRemnants => Set<ExpectedRemnant>();
    public DbSet<MaterialTransaction> MaterialTransactions => Set<MaterialTransaction>();
    public DbSet<ProductionConsumption> ProductionConsumptions => Set<ProductionConsumption>();
    public DbSet<OperationLog> OperationLogs => Set<OperationLog>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<MaterialException> MaterialExceptions => Set<MaterialException>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<VendorPart> VendorParts => Set<VendorPart>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.AppUserId);
            e.Property(x => x.Username).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
        });

        modelBuilder.Entity<MaterialSpec>(e =>
        {
            e.HasKey(x => x.MaterialSpecId);
            e.Property(x => x.MaterialFamily).HasMaxLength(80).IsRequired();
            e.Property(x => x.Grade).HasMaxLength(80);
            e.Property(x => x.Finish).HasMaxLength(80);
            e.Property(x => x.ThicknessIn).HasColumnType("decimal(9,4)").IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.GaugeLabel).HasMaxLength(20);
            e.Property(x => x.NormalizedKey)
                .HasMaxLength(400)
                .HasComputedColumnSql(
                    "LOWER([MaterialFamily]) + '|' + LOWER(ISNULL([Grade], '')) + '|' + " +
                    "LOWER(ISNULL([Finish], '')) + '|' + CONVERT(nvarchar(30), [ThicknessIn])",
                    stored: true);
            e.HasIndex(x => x.NormalizedKey).IsUnique();
        });

        modelBuilder.Entity<Vendor>(e =>
        {
            e.HasKey(x => x.VendorId);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NormalizedName)
                .HasMaxLength(200)
                .HasComputedColumnSql("UPPER(RTRIM(LTRIM([Name])))", stored: true);
            e.Property(x => x.ContactName).HasMaxLength(200);
            e.Property(x => x.ContactEmail).HasMaxLength(320);
            e.Property(x => x.ContactPhone).HasMaxLength(50);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_Vendor_Name_NotBlank", "LEN(LTRIM(RTRIM([Name]))) > 0"));
        });

        modelBuilder.Entity<VendorPart>(e =>
        {
            e.HasKey(x => x.VendorPartId);
            e.Property(x => x.PartNumber).HasMaxLength(100).IsRequired();
            e.Property(x => x.NormalizedPartNumber)
                .HasMaxLength(100)
                .HasComputedColumnSql("UPPER(RTRIM(LTRIM([PartNumber])))", stored: true);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.MinimumOrderQty).HasColumnType("decimal(12,3)");
            e.Property(x => x.Unit).HasMaxLength(50);
            e.HasIndex(x => new { x.VendorId, x.NormalizedPartNumber }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_VendorPart_PartNumber_NotBlank", "LEN(LTRIM(RTRIM([PartNumber]))) > 0"));
            e.HasOne(x => x.Vendor).WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MaterialSpecExternalMapping>(e =>
        {
            e.HasKey(x => x.MaterialSpecExternalMappingId);
            e.Property(x => x.ExternalSystem).HasMaxLength(40).IsRequired();
            e.ToTable(t => t.HasCheckConstraint("CK_MaterialSpecExternalMapping_ExternalSystem", "[ExternalSystem] IN (N'PEP', N'OpenNest')"));
            e.Property(x => x.ExternalKey).HasMaxLength(256).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.ExternalSystem, x.ExternalKey }).IsUnique();
            e.HasOne(x => x.MaterialSpec).WithMany().HasForeignKey(x => x.MaterialSpecId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ApprovedByUser).WithMany().HasForeignKey(x => x.ApprovedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InventoryLocation>(e =>
        {
            e.HasKey(x => x.InventoryLocationId);
            e.Property(x => x.LocationCode).HasMaxLength(80).IsRequired();
            e.HasIndex(x => x.LocationCode).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
            e.Property(x => x.LocationType).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.StorageMode).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(500);
            e.HasOne(x => x.ParentInventoryLocation).WithMany()
                .HasForeignKey(x => x.ParentInventoryLocationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SheetLot>(e =>
        {
            e.HasKey(x => x.SheetLotId);
            e.Property(x => x.VendorName).HasMaxLength(200);
            e.Property(x => x.PurchaseOrderNumber).HasMaxLength(100);
            e.Property(x => x.ReceiptNumber).HasMaxLength(100);
            e.Property(x => x.VendorPartNumber).HasMaxLength(100);
            e.Property(x => x.HeatLotNumber).HasMaxLength(100);
            e.Property(x => x.CostPerUnit).HasColumnType("decimal(12,4)");
            e.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.HasOne(x => x.MaterialSpec).WithMany().HasForeignKey(x => x.MaterialSpecId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PurchaseOrderLine).WithMany().HasForeignKey(x => x.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Vendor).WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.VendorPart).WithMany().HasForeignKey(x => x.VendorPartId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurchaseOrder>(e =>
        {
            e.HasKey(x => x.PurchaseOrderId);
            e.Property(x => x.PurchaseOrderNumber).HasMaxLength(100).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Vendor).WithMany().HasForeignKey(x => x.VendorId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurchaseOrderLine>(e =>
        {
            e.HasKey(x => x.PurchaseOrderLineId);
            e.Property(x => x.VendorDescription).HasMaxLength(200);
            e.Property(x => x.QuantityOrdered).HasColumnType("decimal(12,3)").IsRequired();
            e.Property(x => x.QuantityReceived).HasColumnType("decimal(12,3)").IsRequired();
            e.Property(x => x.UsableWidthIn).HasColumnType("decimal(10,3)");
            e.Property(x => x.UsableLengthIn).HasColumnType("decimal(10,3)");
            e.Property(x => x.UnitPrice).HasColumnType("decimal(12,4)");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.PurchaseOrderId, x.LineNumber }).IsUnique();
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_PurchaseOrderLine_QuantityOrdered",
                    "[QuantityOrdered] = FLOOR([QuantityOrdered]) AND [QuantityOrdered] >= 1");
                t.HasCheckConstraint("CK_PurchaseOrderLine_QuantityReceived",
                    "[QuantityReceived] >= 0 AND [QuantityReceived] = FLOOR([QuantityReceived]) AND [QuantityReceived] <= [QuantityOrdered]");
            });
            e.HasOne(x => x.PurchaseOrder).WithMany(x => x.Lines).HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.MaterialSpec).WithMany().HasForeignKey(x => x.MaterialSpecId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.VendorPart).WithMany().HasForeignKey(x => x.VendorPartId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurchaseOrderLineAllocation>(e =>
        {
            e.HasKey(x => x.PurchaseOrderLineAllocationId);
            e.Property(x => x.QuantityPlanned).HasColumnType("decimal(12,3)").IsRequired();
            e.ToTable(t => t.HasCheckConstraint("CK_PurchaseOrderLineAllocation_QuantityPlanned",
                "[QuantityPlanned] = FLOOR([QuantityPlanned]) AND [QuantityPlanned] >= 1"));
            e.HasOne(x => x.PurchaseOrderLine).WithMany(x => x.LocationAllocations)
                .HasForeignKey(x => x.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.InventoryLocation).WithMany()
                .HasForeignKey(x => x.InventoryLocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CreatedByUser).WithMany()
                .HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InventoryItem>(e =>
        {
            e.HasKey(x => x.InventoryItemId);
            e.Property(x => x.PublicItemCode).HasMaxLength(80).IsRequired();
            e.HasIndex(x => x.PublicItemCode).IsUnique();
            e.Property(x => x.ItemKind).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.QuantityOnHand).HasColumnType("decimal(12,3)").IsRequired();
            e.Property(x => x.UsableWidthIn).HasColumnType("decimal(10,3)");
            e.Property(x => x.UsableLengthIn).HasColumnType("decimal(10,3)");
            e.Property(x => x.ShapeClassification).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.ToTable(t => t.HasCheckConstraint("CK_InventoryItem_QuantityOnHand",
                "([ItemKind] IN (N'FullSheetBundle', N'FullSheet') AND [QuantityOnHand] = FLOOR([QuantityOnHand]) AND [QuantityOnHand] >= 0) " +
                "OR ([ItemKind] = N'Drop' AND [QuantityOnHand] IN (0, 1))"));
            e.HasOne(x => x.MaterialSpec).WithMany().HasForeignKey(x => x.MaterialSpecId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SheetLot).WithMany().HasForeignKey(x => x.SheetLotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.InventoryLocation).WithMany().HasForeignKey(x => x.InventoryLocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.LastObservedByUser).WithMany().HasForeignKey(x => x.LastObservedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MaterialReservation>(e =>
        {
            e.HasKey(x => x.MaterialReservationId);
            e.Property(x => x.ShopOpsJobId).HasMaxLength(100);
            e.Property(x => x.DrawingId).HasMaxLength(100);
            e.Property(x => x.DrawingRevision).HasMaxLength(50);
            e.Property(x => x.PepNestName).HasMaxLength(200);
            e.Property(x => x.NestName).HasMaxLength(200);
            e.HasIndex(x => x.OpenNestNestId);
            e.Property(x => x.Equipment).HasConversion<string?>().HasMaxLength(20);
            e.Property(x => x.LaserProgramRecordId).HasMaxLength(100);
            e.Property(x => x.ReservedAreaSqIn).HasColumnType("decimal(14,3)");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.ExpectedRemainderWidthIn).HasColumnType("decimal(10,3)");
            e.Property(x => x.ExpectedRemainderLengthIn).HasColumnType("decimal(10,3)");
            e.Property(x => x.ExpectedRemainderSourceType).HasConversion<string?>().HasMaxLength(20);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.ToTable(t => t.HasCheckConstraint("CK_MaterialReservation_ReservedQuantity", "[ReservedQuantity] = 1"));
            e.HasOne(x => x.InventoryItem).WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ApprovedByUser).WithMany().HasForeignKey(x => x.ApprovedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExpectedRemnant>(e =>
        {
            e.ToTable("ExpectedRemnants");
            e.HasKey(x => x.ExpectedRemnantId);
            e.HasIndex(x => new { x.MaterialReservationId, x.Sequence }).IsUnique();
            e.Property(x => x.WidthIn).HasColumnType("decimal(10,3)");
            e.Property(x => x.LengthIn).HasColumnType("decimal(10,3)");
            e.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.MaterialReservation).WithMany(x => x.ExpectedRemnants)
                .HasForeignKey(x => x.MaterialReservationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MaterialTransaction>(e =>
        {
            e.HasKey(x => x.MaterialTransactionId);
            e.Property(x => x.TransactionType).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.QuantityDelta).HasColumnType("decimal(12,3)").IsRequired();
            e.Property(x => x.GeometryWidthInSnapshot).HasColumnType("decimal(10,3)");
            e.Property(x => x.GeometryLengthInSnapshot).HasColumnType("decimal(10,3)");
            e.Property(x => x.ReasonCode).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.SourceSystem).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.ExternalReference).HasMaxLength(200);
            e.HasOne(x => x.RecordedByUser).WithMany().HasForeignKey(x => x.RecordedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.InventoryItem).WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.RelatedInventoryItem).WithMany().HasForeignKey(x => x.RelatedInventoryItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.MaterialReservation).WithMany().HasForeignKey(x => x.MaterialReservationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.RelatedInventoryLocation).WithMany().HasForeignKey(x => x.RelatedInventoryLocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SheetLot).WithMany().HasForeignKey(x => x.SheetLotId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProductionConsumption>(e =>
        {
            e.HasKey(x => x.ProductionConsumptionId);
            e.HasIndex(x => x.MaterialReservationId).IsUnique();
            e.Property(x => x.LaserProgramRecordId).HasMaxLength(100);
            e.HasIndex(x => x.LaserProgramRecordId);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.ActualConsumedQuantity).HasColumnType("decimal(12,3)");
            e.Property(x => x.ActualConsumedAreaSqIn).HasColumnType("decimal(14,3)");
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasOne(x => x.MaterialReservation).WithMany().HasForeignKey(x => x.MaterialReservationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PrimaryMaterialTransaction).WithMany().HasForeignKey(x => x.PrimaryMaterialTransactionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ConfirmedByUser).WithMany().HasForeignKey(x => x.ConfirmedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OperationLog>(e =>
        {
            e.HasKey(x => x.CorrelationId);
            e.Property(x => x.CorrelationId).ValueGeneratedNever();
            e.Property(x => x.ProcedureName).HasMaxLength(100).IsRequired();
            e.Property(x => x.RequestPayloadHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.ResultSummary).HasColumnType("nvarchar(max)").IsRequired();
            e.HasOne(x => x.ExecutedByUser).WithMany().HasForeignKey(x => x.ExecutedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Attachment>(e =>
        {
            e.HasKey(x => x.AttachmentId);
            e.Property(x => x.EntityType).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.StoragePath).HasMaxLength(400).IsRequired();
            e.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.ByteLength).IsRequired();
            e.Property(x => x.Checksum).HasMaxLength(100);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasOne(x => x.UploadedByUser).WithMany().HasForeignKey(x => x.UploadedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MaterialException>(e =>
        {
            e.HasKey(x => x.MaterialExceptionId);
            e.Property(x => x.ExceptionType).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.DedupeKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(10).IsRequired();
            e.Property(x => x.ResolutionNotes).HasMaxLength(1000);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasIndex(x => x.DedupeKey).IsUnique().HasFilter("[Status] = N'Open'");
            e.HasOne(x => x.InventoryItem).WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.MaterialReservation).WithMany().HasForeignKey(x => x.MaterialReservationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.OpenedByUser).WithMany().HasForeignKey(x => x.OpenedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ResolvedByUser).WithMany().HasForeignKey(x => x.ResolvedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.HasSequence<long>("ItemCodeSeq", Schema).StartsAt(1).IncrementsBy(1);
    }
}
