-- docs/schema/material-permissions-v4.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, Phase 3.
--
-- Phase 3 introduces the closeout/exception write surface: CloseoutService
-- (MarkReservationAwaitingCloseoutAsync, CloseoutConsumptionAsync) and MaterialExceptionService
-- (ScanAsync, ResolveMaterialExceptionAsync). Per the note at the top of material-permissions-v1.sql,
-- grants arrive with the code that uses them — this file adds ONLY what Phase 3 needs. It does not
-- touch the Phase 0/1/2 grants already applied by material-permissions-v1.sql/v2.sql/v3.sql, and is
-- safe to re-run (GRANT is idempotent).
--
-- Run against the real `MaterialLedger` database using an account with GRANT rights (sa or a member
-- of db_owner/db_securityadmin), the same way material-permissions-v1.sql/v2.sql/v3.sql were applied.
-- This is a manual administrative step — the running application never applies its own grants.

USE MaterialLedger;
GO

-- ProductionConsumptions: new table for Phase 3. CloseoutConsumption inserts exactly one row per
-- successful closeout (enforced by the existing unique index on MaterialReservationId). No
-- UPDATE/DELETE — a closeout outcome, once recorded, is never edited; a correction would be a new
-- MaterialTransaction/Adjustment, not a mutated ProductionConsumption row.
GRANT SELECT, INSERT ON material.ProductionConsumptions TO materialledger_app;

-- MaterialExceptions: new table for Phase 3. ScanAsync inserts newly-detected exceptions;
-- ResolveMaterialException updates Status/ResolvedAtUtc/ResolvedBy/ResolutionNotes on the same row.
-- No DELETE — a resolved exception stays as history; a recurring condition opens a NEW row rather
-- than reusing the old one (spec section 4E), matching every other table in this schema.
GRANT SELECT, INSERT, UPDATE ON material.MaterialExceptions TO materialledger_app;
GO

-- No new grant is needed on InventoryItems (Phase 1/2 already granted SELECT/INSERT/UPDATE, which
-- covers both quantity/status mutation on existing items AND inserting the new remnant InventoryItem
-- row a UsableDropCreated closeout creates), MaterialReservations (Phase 2 already granted
-- SELECT/INSERT/UPDATE, which covers the Approved -> AwaitingCloseout -> Closed*/Exception
-- transitions), MaterialTransactions (Phase 0 already granted SELECT/INSERT for the
-- Consumption/Return/RemnantCreated/Quarantine transaction types this phase writes), or ItemCodeSeq
-- (Phase 1 already granted UPDATE, reused here to mint the new remnant item's PublicItemCode).
