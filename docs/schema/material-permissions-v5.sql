-- docs/schema/material-permissions-v5.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, Phase 4 (ReceiveMaterial).
--
-- ReceiveMaterialService (MaterialLedger.Data/Receiving/ReceiveMaterialService.cs) inserts a new
-- SheetLot row for every receipt. material.SheetLots has had zero grants to materialledger_app
-- through v1-v4 -- this is the only new grant this file adds. It does not touch the Phase 0-3
-- grants already applied by material-permissions-v1.sql/v2.sql/v3.sql/v4.sql, and is safe to
-- re-run (GRANT is idempotent).
--
-- Run against the real `MaterialLedger` database using an account with GRANT rights (sa or a member
-- of db_owner/db_securityadmin), the same way material-permissions-v1.sql through v4.sql were
-- applied. This is a manual administrative step -- the running application never applies its own
-- grants.

USE MaterialLedger;
GO

-- SheetLots: ReceiveMaterialAsync inserts exactly one row per receipt, capturing vendor/PO/receipt/
-- vendor-part/heat-lot/cost provenance. The application login has no UPDATE/DELETE grant -- a
-- SheetLot, once recorded, is never edited or removed. The only historical exception is the
-- administrator-run, transaction/audit-guarded FK enrichment documented in
-- material-data-vendors-backfill-v1.sql; it does not grant the application elevated permissions.
GRANT SELECT, INSERT ON material.SheetLots TO materialledger_app;
GO

-- No new grant is needed on InventoryItems (Phase 1/2 already granted SELECT/INSERT/UPDATE, which
-- covers inserting the new received InventoryItem row), MaterialTransactions (Phase 0 already
-- granted SELECT/INSERT, covering the new Receipt transaction type), MaterialSpecs (Phase 1 already
-- granted SELECT/INSERT for the existing-vs-new-spec picker), or ItemCodeSeq (Phase 1 already
-- granted UPDATE, reused here to mint the new item's PublicItemCode).
