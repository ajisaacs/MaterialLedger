-- docs/schema/material-permissions-v2.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, Phase 1.
--
-- Phase 1 introduces the locations/census write surface: LocationSeeder (startup catalog seed),
-- LocationSlotService.ConfirmLocationSlotsAsync, OpeningBalanceService.RecordOpeningBalanceAsync,
-- and MaterialSpecService's reference-data lookup/creation. Per the note at the top of
-- material-permissions-v1.sql, grants arrive with the code that uses them — this file adds ONLY
-- what Phase 1 needs. It does not touch the Phase 0 grants already applied by
-- material-permissions-v1.sql, and is safe to re-run (GRANT is idempotent).
--
-- Run against the real `MaterialLedger` database using an account with GRANT rights (sa or a
-- member of db_owner/db_securityadmin), the same way material-permissions-v1.sql was applied. This
-- is a manual administrative step — the running application never applies its own grants.

USE MaterialLedger;
GO

-- InventoryLocations: LocationSeeder inserts the rack/skid catalog at startup; LocationSlotService
-- inserts child slots and updates the parent rack's CapacityConfirmed flag during census setup.
GRANT SELECT, INSERT, UPDATE ON material.InventoryLocations TO materialledger_app;

-- InventoryItems: OpeningBalanceService inserts one row per physically observed sheet/drop. No
-- UPDATE/DELETE yet — Phase 1 only ever creates opening-balance items; quantity/status mutation
-- (reservations, consumption) arrives with Phase 2/3's write surface.
GRANT SELECT, INSERT ON material.InventoryItems TO materialledger_app;

-- MaterialSpecs: MaterialSpecService looks up or creates reference rows for the material picked
-- during census. Reference data, not the immutable ledger itself — ordinary UPDATE is intentionally
-- withheld for now since Phase 1 has no edit-existing-spec workflow.
GRANT SELECT, INSERT ON material.MaterialSpecs TO materialledger_app;

-- ItemCodeSeq: OpeningBalanceService calls NEXT VALUE FOR to mint each InventoryItem's
-- PublicItemCode. SQL Server requires UPDATE permission on a sequence object to consume a value
-- from it (NEXT VALUE FOR mutates the sequence's internal counter).
GRANT UPDATE ON material.ItemCodeSeq TO materialledger_app;
GO
