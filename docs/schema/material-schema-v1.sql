IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    IF SCHEMA_ID(N'material') IS NULL EXEC(N'CREATE SCHEMA [material];');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE SEQUENCE [material].[ItemCodeSeq] START WITH 1 INCREMENT BY 1 NO CYCLE;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE TABLE [material].[AppUsers] (
        [AppUserId] int NOT NULL IDENTITY,
        [Username] nvarchar(50) NOT NULL,
        [PasswordHash] nvarchar(256) NOT NULL,
        [DisplayName] nvarchar(100) NOT NULL,
        [Role] nvarchar(20) NOT NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_AppUsers] PRIMARY KEY ([AppUserId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE TABLE [material].[InventoryLocations] (
        [InventoryLocationId] bigint NOT NULL IDENTITY,
        [ParentInventoryLocationId] bigint NULL,
        [LocationCode] nvarchar(80) NOT NULL,
        [DisplayName] nvarchar(160) NOT NULL,
        [LocationType] nvarchar(20) NOT NULL,
        [StorageMode] nvarchar(20) NOT NULL,
        [SortOrder] int NOT NULL,
        [CapacityConfirmed] bit NOT NULL,
        [IsActive] bit NOT NULL,
        [Notes] nvarchar(500) NULL,
        CONSTRAINT [PK_InventoryLocations] PRIMARY KEY ([InventoryLocationId]),
        CONSTRAINT [FK_InventoryLocations_InventoryLocations_ParentInventoryLocationId] FOREIGN KEY ([ParentInventoryLocationId]) REFERENCES [material].[InventoryLocations] ([InventoryLocationId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE TABLE [material].[MaterialSpecs] (
        [MaterialSpecId] bigint NOT NULL IDENTITY,
        [MaterialFamily] nvarchar(80) NOT NULL,
        [Grade] nvarchar(80) NULL,
        [Finish] nvarchar(80) NULL,
        [ThicknessIn] decimal(9,4) NOT NULL,
        [DisplayName] nvarchar(200) NOT NULL,
        [IsActive] bit NOT NULL,
        [NormalizedKey] AS LOWER([MaterialFamily]) + '|' + LOWER(ISNULL([Grade], '')) + '|' + LOWER(ISNULL([Finish], '')) + '|' + CONVERT(nvarchar(30), [ThicknessIn]) PERSISTED,
        CONSTRAINT [PK_MaterialSpecs] PRIMARY KEY ([MaterialSpecId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE TABLE [material].[Attachments] (
        [AttachmentId] bigint NOT NULL IDENTITY,
        [EntityType] nvarchar(30) NOT NULL,
        [EntityId] bigint NOT NULL,
        [StoragePath] nvarchar(400) NOT NULL,
        [ContentType] nvarchar(100) NULL,
        [Checksum] nvarchar(100) NULL,
        [UploadedAtUtc] datetime2 NOT NULL,
        [UploadedBy] int NOT NULL,
        CONSTRAINT [PK_Attachments] PRIMARY KEY ([AttachmentId]),
        CONSTRAINT [FK_Attachments_AppUsers_UploadedBy] FOREIGN KEY ([UploadedBy]) REFERENCES [material].[AppUsers] ([AppUserId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE TABLE [material].[OperationLogs] (
        [CorrelationId] uniqueidentifier NOT NULL,
        [ProcedureName] nvarchar(100) NOT NULL,
        [RequestPayloadHash] nvarchar(128) NOT NULL,
        [ResultSummary] nvarchar(max) NOT NULL,
        [ExecutedAtUtc] datetime2 NOT NULL,
        [ExecutedBy] int NOT NULL,
        CONSTRAINT [PK_OperationLogs] PRIMARY KEY ([CorrelationId]),
        CONSTRAINT [FK_OperationLogs_AppUsers_ExecutedBy] FOREIGN KEY ([ExecutedBy]) REFERENCES [material].[AppUsers] ([AppUserId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE TABLE [material].[MaterialSpecExternalMappings] (
        [MaterialSpecExternalMappingId] bigint NOT NULL IDENTITY,
        [MaterialSpecId] bigint NOT NULL,
        [ExternalSystem] nvarchar(40) NOT NULL,
        [ExternalKey] nvarchar(256) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [ApprovedAtUtc] datetime2 NULL,
        [ApprovedBy] int NULL,
        CONSTRAINT [PK_MaterialSpecExternalMappings] PRIMARY KEY ([MaterialSpecExternalMappingId]),
        CONSTRAINT [CK_MaterialSpecExternalMapping_ExternalSystem] CHECK ([ExternalSystem] IN (N'PEP')),
        CONSTRAINT [FK_MaterialSpecExternalMappings_AppUsers_ApprovedBy] FOREIGN KEY ([ApprovedBy]) REFERENCES [material].[AppUsers] ([AppUserId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MaterialSpecExternalMappings_MaterialSpecs_MaterialSpecId] FOREIGN KEY ([MaterialSpecId]) REFERENCES [material].[MaterialSpecs] ([MaterialSpecId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE TABLE [material].[SheetLots] (
        [SheetLotId] bigint NOT NULL IDENTITY,
        [MaterialSpecId] bigint NOT NULL,
        [VendorName] nvarchar(200) NULL,
        [PurchaseOrderNumber] nvarchar(100) NULL,
        [ReceiptNumber] nvarchar(100) NULL,
        [VendorPartNumber] nvarchar(100) NULL,
        [HeatLotNumber] nvarchar(100) NULL,
        [CostPerUnit] decimal(12,4) NULL,
        [ReceivedAtUtc] datetime2 NULL,
        [SourceType] nvarchar(20) NOT NULL,
        CONSTRAINT [PK_SheetLots] PRIMARY KEY ([SheetLotId]),
        CONSTRAINT [FK_SheetLots_MaterialSpecs_MaterialSpecId] FOREIGN KEY ([MaterialSpecId]) REFERENCES [material].[MaterialSpecs] ([MaterialSpecId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE TABLE [material].[InventoryItems] (
        [InventoryItemId] bigint NOT NULL IDENTITY,
        [PublicItemCode] nvarchar(80) NOT NULL,
        [MaterialSpecId] bigint NOT NULL,
        [SheetLotId] bigint NULL,
        [InventoryLocationId] bigint NOT NULL,
        [ItemKind] nvarchar(20) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [QuantityOnHand] decimal(12,3) NOT NULL,
        [UsableWidthIn] decimal(10,3) NULL,
        [UsableLengthIn] decimal(10,3) NULL,
        [ShapeClassification] nvarchar(30) NOT NULL,
        [LastObservedAtUtc] datetime2 NOT NULL,
        [LastObservedBy] int NOT NULL,
        [Notes] nvarchar(1000) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_InventoryItems] PRIMARY KEY ([InventoryItemId]),
        CONSTRAINT [CK_InventoryItem_QuantityOnHand] CHECK (([ItemKind] IN (N'FullSheetBundle', N'FullSheet') AND [QuantityOnHand] = FLOOR([QuantityOnHand]) AND [QuantityOnHand] >= 0) OR ([ItemKind] = N'Drop' AND [QuantityOnHand] IN (0, 1))),
        CONSTRAINT [FK_InventoryItems_AppUsers_LastObservedBy] FOREIGN KEY ([LastObservedBy]) REFERENCES [material].[AppUsers] ([AppUserId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_InventoryItems_InventoryLocations_InventoryLocationId] FOREIGN KEY ([InventoryLocationId]) REFERENCES [material].[InventoryLocations] ([InventoryLocationId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_InventoryItems_MaterialSpecs_MaterialSpecId] FOREIGN KEY ([MaterialSpecId]) REFERENCES [material].[MaterialSpecs] ([MaterialSpecId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_InventoryItems_SheetLots_SheetLotId] FOREIGN KEY ([SheetLotId]) REFERENCES [material].[SheetLots] ([SheetLotId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE TABLE [material].[MaterialReservations] (
        [MaterialReservationId] bigint NOT NULL IDENTITY,
        [InventoryItemId] bigint NOT NULL,
        [ShopOpsJobId] nvarchar(100) NULL,
        [DrawingId] nvarchar(100) NULL,
        [DrawingRevision] nvarchar(50) NULL,
        [PepNestYear] int NULL,
        [PepNestName] nvarchar(200) NULL,
        [Equipment] nvarchar(20) NULL,
        [LaserProgramRecordId] nvarchar(100) NULL,
        [ReservedQuantity] int NOT NULL,
        [ReservedAreaSqIn] decimal(14,3) NULL,
        [Status] nvarchar(20) NOT NULL,
        [ScheduledForUtc] datetime2 NULL,
        [ApprovedAtUtc] datetime2 NULL,
        [ApprovedBy] int NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [CreatedBy] int NOT NULL,
        [ExpectedRemainderWidthIn] decimal(10,3) NULL,
        [ExpectedRemainderLengthIn] decimal(10,3) NULL,
        [ExpectedRemainderSourceType] nvarchar(20) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_MaterialReservations] PRIMARY KEY ([MaterialReservationId]),
        CONSTRAINT [CK_MaterialReservation_ReservedQuantity] CHECK ([ReservedQuantity] = 1),
        CONSTRAINT [FK_MaterialReservations_AppUsers_ApprovedBy] FOREIGN KEY ([ApprovedBy]) REFERENCES [material].[AppUsers] ([AppUserId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MaterialReservations_AppUsers_CreatedBy] FOREIGN KEY ([CreatedBy]) REFERENCES [material].[AppUsers] ([AppUserId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MaterialReservations_InventoryItems_InventoryItemId] FOREIGN KEY ([InventoryItemId]) REFERENCES [material].[InventoryItems] ([InventoryItemId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE TABLE [material].[MaterialExceptions] (
        [MaterialExceptionId] bigint NOT NULL IDENTITY,
        [ExceptionType] nvarchar(40) NOT NULL,
        [InventoryItemId] bigint NULL,
        [MaterialReservationId] bigint NULL,
        [DedupeKey] nvarchar(200) NOT NULL,
        [Status] nvarchar(10) NOT NULL,
        [OpenedAtUtc] datetime2 NOT NULL,
        [OpenedBy] int NULL,
        [ResolvedAtUtc] datetime2 NULL,
        [ResolvedBy] int NULL,
        [ResolutionNotes] nvarchar(1000) NULL,
        [Notes] nvarchar(1000) NULL,
        CONSTRAINT [PK_MaterialExceptions] PRIMARY KEY ([MaterialExceptionId]),
        CONSTRAINT [FK_MaterialExceptions_AppUsers_OpenedBy] FOREIGN KEY ([OpenedBy]) REFERENCES [material].[AppUsers] ([AppUserId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MaterialExceptions_AppUsers_ResolvedBy] FOREIGN KEY ([ResolvedBy]) REFERENCES [material].[AppUsers] ([AppUserId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MaterialExceptions_InventoryItems_InventoryItemId] FOREIGN KEY ([InventoryItemId]) REFERENCES [material].[InventoryItems] ([InventoryItemId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MaterialExceptions_MaterialReservations_MaterialReservationId] FOREIGN KEY ([MaterialReservationId]) REFERENCES [material].[MaterialReservations] ([MaterialReservationId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE TABLE [material].[MaterialTransactions] (
        [MaterialTransactionId] bigint NOT NULL IDENTITY,
        [OccurredAtUtc] datetime2 NOT NULL,
        [RecordedAtUtc] datetime2 NOT NULL,
        [RecordedBy] int NOT NULL,
        [TransactionType] nvarchar(30) NOT NULL,
        [InventoryItemId] bigint NOT NULL,
        [RelatedInventoryItemId] bigint NULL,
        [MaterialReservationId] bigint NULL,
        [RelatedInventoryLocationId] bigint NULL,
        [SheetLotId] bigint NULL,
        [QuantityDelta] decimal(12,3) NOT NULL,
        [GeometryWidthInSnapshot] decimal(10,3) NULL,
        [GeometryLengthInSnapshot] decimal(10,3) NULL,
        [ReasonCode] nvarchar(100) NULL,
        [Notes] nvarchar(1000) NULL,
        [SourceSystem] nvarchar(30) NOT NULL,
        [ExternalReference] nvarchar(200) NULL,
        CONSTRAINT [PK_MaterialTransactions] PRIMARY KEY ([MaterialTransactionId]),
        CONSTRAINT [FK_MaterialTransactions_AppUsers_RecordedBy] FOREIGN KEY ([RecordedBy]) REFERENCES [material].[AppUsers] ([AppUserId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MaterialTransactions_InventoryItems_InventoryItemId] FOREIGN KEY ([InventoryItemId]) REFERENCES [material].[InventoryItems] ([InventoryItemId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MaterialTransactions_InventoryItems_RelatedInventoryItemId] FOREIGN KEY ([RelatedInventoryItemId]) REFERENCES [material].[InventoryItems] ([InventoryItemId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MaterialTransactions_InventoryLocations_RelatedInventoryLocationId] FOREIGN KEY ([RelatedInventoryLocationId]) REFERENCES [material].[InventoryLocations] ([InventoryLocationId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MaterialTransactions_MaterialReservations_MaterialReservationId] FOREIGN KEY ([MaterialReservationId]) REFERENCES [material].[MaterialReservations] ([MaterialReservationId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MaterialTransactions_SheetLots_SheetLotId] FOREIGN KEY ([SheetLotId]) REFERENCES [material].[SheetLots] ([SheetLotId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE TABLE [material].[ProductionConsumptions] (
        [ProductionConsumptionId] bigint NOT NULL IDENTITY,
        [MaterialReservationId] bigint NOT NULL,
        [PrimaryMaterialTransactionId] bigint NOT NULL,
        [LaserProgramRecordId] nvarchar(100) NULL,
        [ConfirmedAtUtc] datetime2 NOT NULL,
        [ConfirmedBy] int NOT NULL,
        [Outcome] nvarchar(30) NOT NULL,
        [ActualConsumedQuantity] decimal(12,3) NULL,
        [ActualConsumedAreaSqIn] decimal(14,3) NULL,
        [Notes] nvarchar(1000) NULL,
        CONSTRAINT [PK_ProductionConsumptions] PRIMARY KEY ([ProductionConsumptionId]),
        CONSTRAINT [FK_ProductionConsumptions_AppUsers_ConfirmedBy] FOREIGN KEY ([ConfirmedBy]) REFERENCES [material].[AppUsers] ([AppUserId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ProductionConsumptions_MaterialReservations_MaterialReservationId] FOREIGN KEY ([MaterialReservationId]) REFERENCES [material].[MaterialReservations] ([MaterialReservationId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ProductionConsumptions_MaterialTransactions_PrimaryMaterialTransactionId] FOREIGN KEY ([PrimaryMaterialTransactionId]) REFERENCES [material].[MaterialTransactions] ([MaterialTransactionId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_AppUsers_Username] ON [material].[AppUsers] ([Username]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Attachments_EntityType_EntityId] ON [material].[Attachments] ([EntityType], [EntityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Attachments_UploadedBy] ON [material].[Attachments] ([UploadedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InventoryItems_InventoryLocationId] ON [material].[InventoryItems] ([InventoryLocationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InventoryItems_LastObservedBy] ON [material].[InventoryItems] ([LastObservedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InventoryItems_MaterialSpecId] ON [material].[InventoryItems] ([MaterialSpecId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_InventoryItems_PublicItemCode] ON [material].[InventoryItems] ([PublicItemCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InventoryItems_SheetLotId] ON [material].[InventoryItems] ([SheetLotId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_InventoryLocations_LocationCode] ON [material].[InventoryLocations] ([LocationCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InventoryLocations_ParentInventoryLocationId] ON [material].[InventoryLocations] ([ParentInventoryLocationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_MaterialExceptions_DedupeKey] ON [material].[MaterialExceptions] ([DedupeKey]) WHERE [Status] = N''Open''');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialExceptions_InventoryItemId] ON [material].[MaterialExceptions] ([InventoryItemId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialExceptions_MaterialReservationId] ON [material].[MaterialExceptions] ([MaterialReservationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialExceptions_OpenedBy] ON [material].[MaterialExceptions] ([OpenedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialExceptions_ResolvedBy] ON [material].[MaterialExceptions] ([ResolvedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialReservations_ApprovedBy] ON [material].[MaterialReservations] ([ApprovedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialReservations_CreatedBy] ON [material].[MaterialReservations] ([CreatedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialReservations_InventoryItemId] ON [material].[MaterialReservations] ([InventoryItemId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialSpecExternalMappings_ApprovedBy] ON [material].[MaterialSpecExternalMappings] ([ApprovedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MaterialSpecExternalMappings_ExternalSystem_ExternalKey] ON [material].[MaterialSpecExternalMappings] ([ExternalSystem], [ExternalKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialSpecExternalMappings_MaterialSpecId] ON [material].[MaterialSpecExternalMappings] ([MaterialSpecId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MaterialSpecs_NormalizedKey] ON [material].[MaterialSpecs] ([NormalizedKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialTransactions_InventoryItemId] ON [material].[MaterialTransactions] ([InventoryItemId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialTransactions_MaterialReservationId] ON [material].[MaterialTransactions] ([MaterialReservationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialTransactions_RecordedBy] ON [material].[MaterialTransactions] ([RecordedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialTransactions_RelatedInventoryItemId] ON [material].[MaterialTransactions] ([RelatedInventoryItemId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialTransactions_RelatedInventoryLocationId] ON [material].[MaterialTransactions] ([RelatedInventoryLocationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MaterialTransactions_SheetLotId] ON [material].[MaterialTransactions] ([SheetLotId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_OperationLogs_ExecutedBy] ON [material].[OperationLogs] ([ExecutedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ProductionConsumptions_ConfirmedBy] ON [material].[ProductionConsumptions] ([ConfirmedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ProductionConsumptions_LaserProgramRecordId] ON [material].[ProductionConsumptions] ([LaserProgramRecordId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ProductionConsumptions_MaterialReservationId] ON [material].[ProductionConsumptions] ([MaterialReservationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ProductionConsumptions_PrimaryMaterialTransactionId] ON [material].[ProductionConsumptions] ([PrimaryMaterialTransactionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SheetLots_MaterialSpecId] ON [material].[SheetLots] ([MaterialSpecId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807180858_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260807180858_InitialCreate', N'9.0.8');
END;

COMMIT;
GO

