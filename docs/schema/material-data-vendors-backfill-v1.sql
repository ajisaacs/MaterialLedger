-- docs/schema/material-data-vendors-backfill-v1.sql
-- MaterialLedger — administrator-only historical vendor association enrichment.
--
-- Prerequisites: apply AddVendorsAndVendorParts and AddVendorReferences first. The application
-- login intentionally has no UPDATE grant on material.SheetLots. This script is the one audited
-- exception: it only fills null association FKs from immutable legacy snapshot text; it never
-- changes a SheetLot snapshot, cost, timestamp, or source type.
--
-- DEFAULT IS PREFLIGHT: all inserts/updates are rolled back. Review the audit and unresolved
-- sample result sets. Set @Commit = 1 only after the preflight has no resolvable orphans, then
-- retain the output with the deployment evidence. Run once using an administrator account.

USE MaterialLedger;
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
-- Required while inserting into tables with indexed persisted computed columns.
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO

DECLARE @ScriptVersion nvarchar(50) = N'material-data-vendors-backfill-v1';
DECLARE @Commit bit = 0; -- PREVIEW ONLY. Administrator changes to 1 for the audited commit run.
DECLARE @StartedAtUtc datetime2(7) = SYSUTCDATETIME();
DECLARE @ExecutingLogin nvarchar(128) = SUSER_SNAME();
DECLARE @VendorSourceCount int = 0;
DECLARE @PartSourceCount int = 0;
DECLARE @VendorsInserted int = 0;
DECLARE @PartsInserted int = 0;
DECLARE @PurchaseOrdersUpdated int = 0;
DECLARE @PurchaseOrderLinesUpdated int = 0;
DECLARE @SheetLotsVendorUpdated int = 0;
DECLARE @SheetLotsVendorPartUpdated int = 0;
DECLARE @BlankPurchaseOrderVendors int = 0;
DECLARE @BlankPurchaseOrderParts int = 0;
DECLARE @BlankSheetLotVendors int = 0;
DECLARE @BlankSheetLotParts int = 0;
DECLARE @OrphanPurchaseOrderVendors int = 0;
DECLARE @OrphanPurchaseOrderParts int = 0;
DECLARE @OrphanSheetLotVendors int = 0;
DECLARE @OrphanSheetLotParts int = 0;
DECLARE @ValidationError nvarchar(2048);

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE #VendorSource
    (
        NormalizedName nvarchar(200) NOT NULL PRIMARY KEY,
        Name nvarchar(200) NOT NULL
    );

    INSERT #VendorSource (NormalizedName, Name)
    SELECT source.NormalizedName, MIN(source.Name)
    FROM
    (
        SELECT UPPER(RTRIM(LTRIM(po.VendorName))) AS NormalizedName,
               LTRIM(RTRIM(po.VendorName)) AS Name
        FROM material.PurchaseOrders po
        WHERE LEN(LTRIM(RTRIM(po.VendorName))) > 0

        UNION ALL

        SELECT UPPER(RTRIM(LTRIM(sl.VendorName))) AS NormalizedName,
               LTRIM(RTRIM(sl.VendorName)) AS Name
        FROM material.SheetLots sl
        WHERE LEN(LTRIM(RTRIM(sl.VendorName))) > 0
    ) source
    GROUP BY source.NormalizedName;

    SET @VendorSourceCount = @@ROWCOUNT;

    INSERT material.Vendors (Name, IsActive, CreatedAtUtc)
    SELECT source.Name, 1, @StartedAtUtc
    FROM #VendorSource source
    LEFT JOIN material.Vendors vendor ON vendor.NormalizedName = source.NormalizedName
    WHERE vendor.VendorId IS NULL;

    SET @VendorsInserted = @@ROWCOUNT;

    CREATE TABLE #VendorPartSource
    (
        VendorId bigint NOT NULL,
        NormalizedPartNumber nvarchar(100) NOT NULL,
        PartNumber nvarchar(100) NOT NULL,
        PRIMARY KEY (VendorId, NormalizedPartNumber)
    );

    INSERT #VendorPartSource (VendorId, NormalizedPartNumber, PartNumber)
    SELECT source.VendorId, source.NormalizedPartNumber, MIN(source.PartNumber)
    FROM
    (
        SELECT vendor.VendorId,
               UPPER(RTRIM(LTRIM(line.VendorPartNumber))) AS NormalizedPartNumber,
               LTRIM(RTRIM(line.VendorPartNumber)) AS PartNumber
        FROM material.PurchaseOrderLines line
        JOIN material.PurchaseOrders po ON po.PurchaseOrderId = line.PurchaseOrderId
        JOIN material.Vendors vendor ON vendor.NormalizedName = UPPER(RTRIM(LTRIM(po.VendorName)))
        WHERE LEN(LTRIM(RTRIM(po.VendorName))) > 0
          AND LEN(LTRIM(RTRIM(line.VendorPartNumber))) > 0

        UNION ALL

        SELECT vendor.VendorId,
               UPPER(RTRIM(LTRIM(sl.VendorPartNumber))) AS NormalizedPartNumber,
               LTRIM(RTRIM(sl.VendorPartNumber)) AS PartNumber
        FROM material.SheetLots sl
        JOIN material.Vendors vendor ON vendor.NormalizedName = UPPER(RTRIM(LTRIM(sl.VendorName)))
        WHERE LEN(LTRIM(RTRIM(sl.VendorName))) > 0
          AND LEN(LTRIM(RTRIM(sl.VendorPartNumber))) > 0
    ) source
    GROUP BY source.VendorId, source.NormalizedPartNumber;

    SET @PartSourceCount = @@ROWCOUNT;

    INSERT material.VendorParts (VendorId, PartNumber, IsActive, CreatedAtUtc)
    SELECT source.VendorId, source.PartNumber, 1, @StartedAtUtc
    FROM #VendorPartSource source
    LEFT JOIN material.VendorParts part
        ON part.VendorId = source.VendorId
       AND part.NormalizedPartNumber = source.NormalizedPartNumber
    WHERE part.VendorPartId IS NULL;

    SET @PartsInserted = @@ROWCOUNT;

    UPDATE po
       SET VendorId = vendor.VendorId
    FROM material.PurchaseOrders po
    JOIN material.Vendors vendor ON vendor.NormalizedName = UPPER(RTRIM(LTRIM(po.VendorName)))
    WHERE po.VendorId IS NULL
      AND LEN(LTRIM(RTRIM(po.VendorName))) > 0;

    SET @PurchaseOrdersUpdated = @@ROWCOUNT;

    UPDATE line
       SET VendorPartId = part.VendorPartId
    FROM material.PurchaseOrderLines line
    JOIN material.PurchaseOrders po ON po.PurchaseOrderId = line.PurchaseOrderId
    JOIN material.VendorParts part
        ON part.VendorId = po.VendorId
       AND part.NormalizedPartNumber = UPPER(RTRIM(LTRIM(line.VendorPartNumber)))
    WHERE line.VendorPartId IS NULL
      AND po.VendorId IS NOT NULL
      AND LEN(LTRIM(RTRIM(line.VendorPartNumber))) > 0;

    SET @PurchaseOrderLinesUpdated = @@ROWCOUNT;

    UPDATE sl
       SET VendorId = vendor.VendorId
    FROM material.SheetLots sl
    JOIN material.Vendors vendor ON vendor.NormalizedName = UPPER(RTRIM(LTRIM(sl.VendorName)))
    WHERE sl.VendorId IS NULL
      AND LEN(LTRIM(RTRIM(sl.VendorName))) > 0;

    SET @SheetLotsVendorUpdated = @@ROWCOUNT;

    UPDATE sl
       SET VendorPartId = part.VendorPartId
    FROM material.SheetLots sl
    JOIN material.VendorParts part
        ON part.VendorId = sl.VendorId
       AND part.NormalizedPartNumber = UPPER(RTRIM(LTRIM(sl.VendorPartNumber)))
    WHERE sl.VendorPartId IS NULL
      AND sl.VendorId IS NOT NULL
      AND LEN(LTRIM(RTRIM(sl.VendorPartNumber))) > 0;

    SET @SheetLotsVendorPartUpdated = @@ROWCOUNT;

    SELECT @BlankPurchaseOrderVendors = COUNT(*)
    FROM material.PurchaseOrders
    WHERE LEN(LTRIM(RTRIM(VendorName))) = 0;

    SELECT @BlankPurchaseOrderParts = COUNT(*)
    FROM material.PurchaseOrderLines
    WHERE LEN(LTRIM(RTRIM(VendorPartNumber))) = 0;

    SELECT @BlankSheetLotVendors = COUNT(*)
    FROM material.SheetLots
    WHERE LEN(LTRIM(RTRIM(VendorName))) = 0;

    SELECT @BlankSheetLotParts = COUNT(*)
    FROM material.SheetLots
    WHERE LEN(LTRIM(RTRIM(VendorPartNumber))) = 0;

    SELECT @OrphanPurchaseOrderVendors = COUNT(*)
    FROM material.PurchaseOrders
    WHERE LEN(LTRIM(RTRIM(VendorName))) > 0
      AND VendorId IS NULL;

    SELECT @OrphanPurchaseOrderParts = COUNT(*)
    FROM material.PurchaseOrderLines line
    JOIN material.PurchaseOrders po ON po.PurchaseOrderId = line.PurchaseOrderId
    WHERE LEN(LTRIM(RTRIM(line.VendorPartNumber))) > 0
      AND (po.VendorId IS NULL OR line.VendorPartId IS NULL);

    SELECT @OrphanSheetLotVendors = COUNT(*)
    FROM material.SheetLots
    WHERE LEN(LTRIM(RTRIM(VendorName))) > 0
      AND VendorId IS NULL;

    SELECT @OrphanSheetLotParts = COUNT(*)
    FROM material.SheetLots
    WHERE LEN(LTRIM(RTRIM(VendorName))) > 0
      AND LEN(LTRIM(RTRIM(VendorPartNumber))) > 0
      AND (VendorId IS NULL OR VendorPartId IS NULL);

    -- Preserve a bounded unresolved sample in the client/deployment record before a failed commit.
    SELECT TOP (100) N'PurchaseOrderVendor' AS Source, po.PurchaseOrderId AS RecordId,
           po.VendorName AS VendorSnapshot, CAST(NULL AS nvarchar(100)) AS PartSnapshot
    FROM material.PurchaseOrders po
    WHERE LEN(LTRIM(RTRIM(po.VendorName))) > 0 AND po.VendorId IS NULL
    UNION ALL
    SELECT TOP (100) N'PurchaseOrderPart', line.PurchaseOrderLineId,
           po.VendorName, line.VendorPartNumber
    FROM material.PurchaseOrderLines line
    JOIN material.PurchaseOrders po ON po.PurchaseOrderId = line.PurchaseOrderId
    WHERE LEN(LTRIM(RTRIM(line.VendorPartNumber))) > 0
      AND (po.VendorId IS NULL OR line.VendorPartId IS NULL)
    UNION ALL
    SELECT TOP (100) N'SheetLotVendor', sl.SheetLotId, sl.VendorName, sl.VendorPartNumber
    FROM material.SheetLots sl
    WHERE LEN(LTRIM(RTRIM(sl.VendorName))) > 0 AND sl.VendorId IS NULL
    UNION ALL
    SELECT TOP (100) N'SheetLotPart', sl.SheetLotId, sl.VendorName, sl.VendorPartNumber
    FROM material.SheetLots sl
    WHERE LEN(LTRIM(RTRIM(sl.VendorName))) > 0
      AND LEN(LTRIM(RTRIM(sl.VendorPartNumber))) > 0
      AND (sl.VendorId IS NULL OR sl.VendorPartId IS NULL);

    SELECT @ValidationError = CONCAT(
        N'Backfill validation failed. PO vendor orphans=', @OrphanPurchaseOrderVendors,
        N'; PO part orphans=', @OrphanPurchaseOrderParts,
        N'; SheetLot vendor orphans=', @OrphanSheetLotVendors,
        N'; SheetLot part orphans=', @OrphanSheetLotParts, N'.')
    WHERE @OrphanPurchaseOrderVendors + @OrphanPurchaseOrderParts
        + @OrphanSheetLotVendors + @OrphanSheetLotParts > 0;

    IF @ValidationError IS NOT NULL
        THROW 51000, @ValidationError, 1;

    SELECT @ScriptVersion AS ScriptVersion,
           @Commit AS CommitMode,
           @StartedAtUtc AS StartedAtUtc,
           SYSUTCDATETIME() AS CompletedAtUtc,
           @ExecutingLogin AS ExecutingLogin,
           @VendorSourceCount AS VendorSourceCount,
           @PartSourceCount AS VendorPartSourceCount,
           @VendorsInserted AS VendorsInserted,
           @PartsInserted AS VendorPartsInserted,
           @PurchaseOrdersUpdated AS PurchaseOrdersUpdated,
           @PurchaseOrderLinesUpdated AS PurchaseOrderLinesUpdated,
           @SheetLotsVendorUpdated AS SheetLotsVendorUpdated,
           @SheetLotsVendorPartUpdated AS SheetLotsVendorPartUpdated,
           @BlankPurchaseOrderVendors AS BlankPurchaseOrderVendors,
           @BlankPurchaseOrderParts AS BlankPurchaseOrderParts,
           @BlankSheetLotVendors AS BlankSheetLotVendors,
           @BlankSheetLotParts AS BlankSheetLotParts,
           @OrphanPurchaseOrderVendors AS OrphanPurchaseOrderVendors,
           @OrphanPurchaseOrderParts AS OrphanPurchaseOrderParts,
           @OrphanSheetLotVendors AS OrphanSheetLotVendors,
           @OrphanSheetLotParts AS OrphanSheetLotParts;

    IF @Commit = 1
        COMMIT TRANSACTION;
    ELSE
        ROLLBACK TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    SELECT @ScriptVersion AS ScriptVersion,
           @Commit AS CommitMode,
           @StartedAtUtc AS StartedAtUtc,
           SYSUTCDATETIME() AS FailedAtUtc,
           @ExecutingLogin AS ExecutingLogin,
           ERROR_NUMBER() AS ErrorNumber,
           ERROR_MESSAGE() AS ErrorMessage;
    THROW;
END CATCH;
GO
