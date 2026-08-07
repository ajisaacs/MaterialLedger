using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "material");

            migrationBuilder.CreateSequence(
                name: "ItemCodeSeq",
                schema: "material");

            migrationBuilder.CreateTable(
                name: "AppUsers",
                schema: "material",
                columns: table => new
                {
                    AppUserId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Username = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.AppUserId);
                });

            migrationBuilder.CreateTable(
                name: "InventoryLocations",
                schema: "material",
                columns: table => new
                {
                    InventoryLocationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ParentInventoryLocationId = table.Column<long>(type: "bigint", nullable: true),
                    LocationCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    LocationType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StorageMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CapacityConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryLocations", x => x.InventoryLocationId);
                    table.ForeignKey(
                        name: "FK_InventoryLocations_InventoryLocations_ParentInventoryLocationId",
                        column: x => x.ParentInventoryLocationId,
                        principalSchema: "material",
                        principalTable: "InventoryLocations",
                        principalColumn: "InventoryLocationId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaterialSpecs",
                schema: "material",
                columns: table => new
                {
                    MaterialSpecId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MaterialFamily = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Grade = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Finish = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ThicknessIn = table.Column<decimal>(type: "decimal(9,4)", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    NormalizedKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, computedColumnSql: "LOWER([MaterialFamily]) + '|' + LOWER(ISNULL([Grade], '')) + '|' + LOWER(ISNULL([Finish], '')) + '|' + CONVERT(nvarchar(30), [ThicknessIn])", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialSpecs", x => x.MaterialSpecId);
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                schema: "material",
                columns: table => new
                {
                    AttachmentId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EntityType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EntityId = table.Column<long>(type: "bigint", nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Checksum = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UploadedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.AttachmentId);
                    table.ForeignKey(
                        name: "FK_Attachments_AppUsers_UploadedBy",
                        column: x => x.UploadedBy,
                        principalSchema: "material",
                        principalTable: "AppUsers",
                        principalColumn: "AppUserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperationLogs",
                schema: "material",
                columns: table => new
                {
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcedureName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequestPayloadHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ResultSummary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExecutedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExecutedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationLogs", x => x.CorrelationId);
                    table.ForeignKey(
                        name: "FK_OperationLogs_AppUsers_ExecutedBy",
                        column: x => x.ExecutedBy,
                        principalSchema: "material",
                        principalTable: "AppUsers",
                        principalColumn: "AppUserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaterialSpecExternalMappings",
                schema: "material",
                columns: table => new
                {
                    MaterialSpecExternalMappingId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MaterialSpecId = table.Column<long>(type: "bigint", nullable: false),
                    ExternalSystem = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ExternalKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovedBy = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialSpecExternalMappings", x => x.MaterialSpecExternalMappingId);
                    table.CheckConstraint("CK_MaterialSpecExternalMapping_ExternalSystem", "[ExternalSystem] IN (N'PEP')");
                    table.ForeignKey(
                        name: "FK_MaterialSpecExternalMappings_AppUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "material",
                        principalTable: "AppUsers",
                        principalColumn: "AppUserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialSpecExternalMappings_MaterialSpecs_MaterialSpecId",
                        column: x => x.MaterialSpecId,
                        principalSchema: "material",
                        principalTable: "MaterialSpecs",
                        principalColumn: "MaterialSpecId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SheetLots",
                schema: "material",
                columns: table => new
                {
                    SheetLotId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MaterialSpecId = table.Column<long>(type: "bigint", nullable: false),
                    VendorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PurchaseOrderNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReceiptNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    VendorPartNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    HeatLotNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CostPerUnit = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SourceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetLots", x => x.SheetLotId);
                    table.ForeignKey(
                        name: "FK_SheetLots_MaterialSpecs_MaterialSpecId",
                        column: x => x.MaterialSpecId,
                        principalSchema: "material",
                        principalTable: "MaterialSpecs",
                        principalColumn: "MaterialSpecId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryItems",
                schema: "material",
                columns: table => new
                {
                    InventoryItemId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PublicItemCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    MaterialSpecId = table.Column<long>(type: "bigint", nullable: false),
                    SheetLotId = table.Column<long>(type: "bigint", nullable: true),
                    InventoryLocationId = table.Column<long>(type: "bigint", nullable: false),
                    ItemKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    QuantityOnHand = table.Column<decimal>(type: "decimal(12,3)", nullable: false),
                    UsableWidthIn = table.Column<decimal>(type: "decimal(10,3)", nullable: true),
                    UsableLengthIn = table.Column<decimal>(type: "decimal(10,3)", nullable: true),
                    ShapeClassification = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    LastObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastObservedBy = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryItems", x => x.InventoryItemId);
                    table.CheckConstraint("CK_InventoryItem_QuantityOnHand", "([ItemKind] IN (N'FullSheetBundle', N'FullSheet') AND [QuantityOnHand] = FLOOR([QuantityOnHand]) AND [QuantityOnHand] >= 0) OR ([ItemKind] = N'Drop' AND [QuantityOnHand] IN (0, 1))");
                    table.ForeignKey(
                        name: "FK_InventoryItems_AppUsers_LastObservedBy",
                        column: x => x.LastObservedBy,
                        principalSchema: "material",
                        principalTable: "AppUsers",
                        principalColumn: "AppUserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryItems_InventoryLocations_InventoryLocationId",
                        column: x => x.InventoryLocationId,
                        principalSchema: "material",
                        principalTable: "InventoryLocations",
                        principalColumn: "InventoryLocationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryItems_MaterialSpecs_MaterialSpecId",
                        column: x => x.MaterialSpecId,
                        principalSchema: "material",
                        principalTable: "MaterialSpecs",
                        principalColumn: "MaterialSpecId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryItems_SheetLots_SheetLotId",
                        column: x => x.SheetLotId,
                        principalSchema: "material",
                        principalTable: "SheetLots",
                        principalColumn: "SheetLotId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaterialReservations",
                schema: "material",
                columns: table => new
                {
                    MaterialReservationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InventoryItemId = table.Column<long>(type: "bigint", nullable: false),
                    ShopOpsJobId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DrawingId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DrawingRevision = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PepNestYear = table.Column<int>(type: "int", nullable: true),
                    PepNestName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Equipment = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LaserProgramRecordId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReservedQuantity = table.Column<int>(type: "int", nullable: false),
                    ReservedAreaSqIn = table.Column<decimal>(type: "decimal(14,3)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScheduledForUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovedBy = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    ExpectedRemainderWidthIn = table.Column<decimal>(type: "decimal(10,3)", nullable: true),
                    ExpectedRemainderLengthIn = table.Column<decimal>(type: "decimal(10,3)", nullable: true),
                    ExpectedRemainderSourceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialReservations", x => x.MaterialReservationId);
                    table.CheckConstraint("CK_MaterialReservation_ReservedQuantity", "[ReservedQuantity] = 1");
                    table.ForeignKey(
                        name: "FK_MaterialReservations_AppUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "material",
                        principalTable: "AppUsers",
                        principalColumn: "AppUserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialReservations_AppUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "material",
                        principalTable: "AppUsers",
                        principalColumn: "AppUserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialReservations_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalSchema: "material",
                        principalTable: "InventoryItems",
                        principalColumn: "InventoryItemId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaterialExceptions",
                schema: "material",
                columns: table => new
                {
                    MaterialExceptionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExceptionType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    InventoryItemId = table.Column<long>(type: "bigint", nullable: true),
                    MaterialReservationId = table.Column<long>(type: "bigint", nullable: true),
                    DedupeKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OpenedBy = table.Column<int>(type: "int", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedBy = table.Column<int>(type: "int", nullable: true),
                    ResolutionNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialExceptions", x => x.MaterialExceptionId);
                    table.ForeignKey(
                        name: "FK_MaterialExceptions_AppUsers_OpenedBy",
                        column: x => x.OpenedBy,
                        principalSchema: "material",
                        principalTable: "AppUsers",
                        principalColumn: "AppUserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialExceptions_AppUsers_ResolvedBy",
                        column: x => x.ResolvedBy,
                        principalSchema: "material",
                        principalTable: "AppUsers",
                        principalColumn: "AppUserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialExceptions_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalSchema: "material",
                        principalTable: "InventoryItems",
                        principalColumn: "InventoryItemId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialExceptions_MaterialReservations_MaterialReservationId",
                        column: x => x.MaterialReservationId,
                        principalSchema: "material",
                        principalTable: "MaterialReservations",
                        principalColumn: "MaterialReservationId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaterialTransactions",
                schema: "material",
                columns: table => new
                {
                    MaterialTransactionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedBy = table.Column<int>(type: "int", nullable: false),
                    TransactionType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    InventoryItemId = table.Column<long>(type: "bigint", nullable: false),
                    RelatedInventoryItemId = table.Column<long>(type: "bigint", nullable: true),
                    MaterialReservationId = table.Column<long>(type: "bigint", nullable: true),
                    RelatedInventoryLocationId = table.Column<long>(type: "bigint", nullable: true),
                    SheetLotId = table.Column<long>(type: "bigint", nullable: true),
                    QuantityDelta = table.Column<decimal>(type: "decimal(12,3)", nullable: false),
                    GeometryWidthInSnapshot = table.Column<decimal>(type: "decimal(10,3)", nullable: true),
                    GeometryLengthInSnapshot = table.Column<decimal>(type: "decimal(10,3)", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SourceSystem = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ExternalReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialTransactions", x => x.MaterialTransactionId);
                    table.ForeignKey(
                        name: "FK_MaterialTransactions_AppUsers_RecordedBy",
                        column: x => x.RecordedBy,
                        principalSchema: "material",
                        principalTable: "AppUsers",
                        principalColumn: "AppUserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialTransactions_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalSchema: "material",
                        principalTable: "InventoryItems",
                        principalColumn: "InventoryItemId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialTransactions_InventoryItems_RelatedInventoryItemId",
                        column: x => x.RelatedInventoryItemId,
                        principalSchema: "material",
                        principalTable: "InventoryItems",
                        principalColumn: "InventoryItemId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialTransactions_InventoryLocations_RelatedInventoryLocationId",
                        column: x => x.RelatedInventoryLocationId,
                        principalSchema: "material",
                        principalTable: "InventoryLocations",
                        principalColumn: "InventoryLocationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialTransactions_MaterialReservations_MaterialReservationId",
                        column: x => x.MaterialReservationId,
                        principalSchema: "material",
                        principalTable: "MaterialReservations",
                        principalColumn: "MaterialReservationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialTransactions_SheetLots_SheetLotId",
                        column: x => x.SheetLotId,
                        principalSchema: "material",
                        principalTable: "SheetLots",
                        principalColumn: "SheetLotId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionConsumptions",
                schema: "material",
                columns: table => new
                {
                    ProductionConsumptionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MaterialReservationId = table.Column<long>(type: "bigint", nullable: false),
                    PrimaryMaterialTransactionId = table.Column<long>(type: "bigint", nullable: false),
                    LaserProgramRecordId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConfirmedBy = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ActualConsumedQuantity = table.Column<decimal>(type: "decimal(12,3)", nullable: true),
                    ActualConsumedAreaSqIn = table.Column<decimal>(type: "decimal(14,3)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionConsumptions", x => x.ProductionConsumptionId);
                    table.ForeignKey(
                        name: "FK_ProductionConsumptions_AppUsers_ConfirmedBy",
                        column: x => x.ConfirmedBy,
                        principalSchema: "material",
                        principalTable: "AppUsers",
                        principalColumn: "AppUserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionConsumptions_MaterialReservations_MaterialReservationId",
                        column: x => x.MaterialReservationId,
                        principalSchema: "material",
                        principalTable: "MaterialReservations",
                        principalColumn: "MaterialReservationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionConsumptions_MaterialTransactions_PrimaryMaterialTransactionId",
                        column: x => x.PrimaryMaterialTransactionId,
                        principalSchema: "material",
                        principalTable: "MaterialTransactions",
                        principalColumn: "MaterialTransactionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_Username",
                schema: "material",
                table: "AppUsers",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_EntityType_EntityId",
                schema: "material",
                table: "Attachments",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_UploadedBy",
                schema: "material",
                table: "Attachments",
                column: "UploadedBy");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_InventoryLocationId",
                schema: "material",
                table: "InventoryItems",
                column: "InventoryLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_LastObservedBy",
                schema: "material",
                table: "InventoryItems",
                column: "LastObservedBy");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_MaterialSpecId",
                schema: "material",
                table: "InventoryItems",
                column: "MaterialSpecId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_PublicItemCode",
                schema: "material",
                table: "InventoryItems",
                column: "PublicItemCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_SheetLotId",
                schema: "material",
                table: "InventoryItems",
                column: "SheetLotId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLocations_LocationCode",
                schema: "material",
                table: "InventoryLocations",
                column: "LocationCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLocations_ParentInventoryLocationId",
                schema: "material",
                table: "InventoryLocations",
                column: "ParentInventoryLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialExceptions_DedupeKey",
                schema: "material",
                table: "MaterialExceptions",
                column: "DedupeKey",
                unique: true,
                filter: "[Status] = N'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialExceptions_InventoryItemId",
                schema: "material",
                table: "MaterialExceptions",
                column: "InventoryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialExceptions_MaterialReservationId",
                schema: "material",
                table: "MaterialExceptions",
                column: "MaterialReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialExceptions_OpenedBy",
                schema: "material",
                table: "MaterialExceptions",
                column: "OpenedBy");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialExceptions_ResolvedBy",
                schema: "material",
                table: "MaterialExceptions",
                column: "ResolvedBy");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialReservations_ApprovedBy",
                schema: "material",
                table: "MaterialReservations",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialReservations_CreatedBy",
                schema: "material",
                table: "MaterialReservations",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialReservations_InventoryItemId",
                schema: "material",
                table: "MaterialReservations",
                column: "InventoryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialSpecExternalMappings_ApprovedBy",
                schema: "material",
                table: "MaterialSpecExternalMappings",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialSpecExternalMappings_ExternalSystem_ExternalKey",
                schema: "material",
                table: "MaterialSpecExternalMappings",
                columns: new[] { "ExternalSystem", "ExternalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaterialSpecExternalMappings_MaterialSpecId",
                schema: "material",
                table: "MaterialSpecExternalMappings",
                column: "MaterialSpecId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialSpecs_NormalizedKey",
                schema: "material",
                table: "MaterialSpecs",
                column: "NormalizedKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaterialTransactions_InventoryItemId",
                schema: "material",
                table: "MaterialTransactions",
                column: "InventoryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialTransactions_MaterialReservationId",
                schema: "material",
                table: "MaterialTransactions",
                column: "MaterialReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialTransactions_RecordedBy",
                schema: "material",
                table: "MaterialTransactions",
                column: "RecordedBy");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialTransactions_RelatedInventoryItemId",
                schema: "material",
                table: "MaterialTransactions",
                column: "RelatedInventoryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialTransactions_RelatedInventoryLocationId",
                schema: "material",
                table: "MaterialTransactions",
                column: "RelatedInventoryLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialTransactions_SheetLotId",
                schema: "material",
                table: "MaterialTransactions",
                column: "SheetLotId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationLogs_ExecutedBy",
                schema: "material",
                table: "OperationLogs",
                column: "ExecutedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionConsumptions_ConfirmedBy",
                schema: "material",
                table: "ProductionConsumptions",
                column: "ConfirmedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionConsumptions_LaserProgramRecordId",
                schema: "material",
                table: "ProductionConsumptions",
                column: "LaserProgramRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionConsumptions_MaterialReservationId",
                schema: "material",
                table: "ProductionConsumptions",
                column: "MaterialReservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionConsumptions_PrimaryMaterialTransactionId",
                schema: "material",
                table: "ProductionConsumptions",
                column: "PrimaryMaterialTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_SheetLots_MaterialSpecId",
                schema: "material",
                table: "SheetLots",
                column: "MaterialSpecId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attachments",
                schema: "material");

            migrationBuilder.DropTable(
                name: "MaterialExceptions",
                schema: "material");

            migrationBuilder.DropTable(
                name: "MaterialSpecExternalMappings",
                schema: "material");

            migrationBuilder.DropTable(
                name: "OperationLogs",
                schema: "material");

            migrationBuilder.DropTable(
                name: "ProductionConsumptions",
                schema: "material");

            migrationBuilder.DropTable(
                name: "MaterialTransactions",
                schema: "material");

            migrationBuilder.DropTable(
                name: "MaterialReservations",
                schema: "material");

            migrationBuilder.DropTable(
                name: "InventoryItems",
                schema: "material");

            migrationBuilder.DropTable(
                name: "AppUsers",
                schema: "material");

            migrationBuilder.DropTable(
                name: "InventoryLocations",
                schema: "material");

            migrationBuilder.DropTable(
                name: "SheetLots",
                schema: "material");

            migrationBuilder.DropTable(
                name: "MaterialSpecs",
                schema: "material");

            migrationBuilder.DropSequence(
                name: "ItemCodeSeq",
                schema: "material");
        }
    }
}
