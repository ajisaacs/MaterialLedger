-- docs/schema/material-permissions-v3.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, Phase 2.
--
-- Phase 2 introduces the reservations/inventory-view write surface: InventoryQueryService
-- (read-only search/summary — no new grants needed beyond existing SELECT), and
-- ReservationService.CreateReservationAsync/ApproveReservationAsync/ReleaseReservationAsync.
-- Per the note at the top of material-permissions-v1.sql, grants arrive with the code that uses
-- them — this file adds ONLY what Phase 2 needs. It does not touch the Phase 0/1 grants already
-- applied by material-permissions-v1.sql/v2.sql, and is safe to re-run (GRANT is idempotent).
--
-- Run against the real `MaterialLedger` database using an account with GRANT rights (sa or a member
-- of db_owner/db_securityadmin), the same way material-permissions-v1.sql/v2.sql were applied. This
-- is a manual administrative step — the running application never applies its own grants.

USE MaterialLedger;
GO

-- InventoryItems: Phase 1 only ever created opening-balance rows (SELECT, INSERT). Phase 2's
-- ApproveReservation/ReleaseReservation flip Status between Available and Reserved for Drop/
-- FullSheet items, so UPDATE is now required. Still no DELETE — items are never removed, only
-- transitioned through status.
GRANT UPDATE ON material.InventoryItems TO materialledger_app;

-- MaterialReservations: new table for Phase 2. CreateReservation inserts; ApproveReservation and
-- ReleaseReservation update Status/ApprovedAtUtc/ApprovedBy on the same row (the RowVersion
-- concurrency check is enforced in application code, not by SQL Server). No DELETE — a released
-- reservation stays as history, matching every other table in this schema.
GRANT SELECT, INSERT, UPDATE ON material.MaterialReservations TO materialledger_app;
GO

-- No new grant is needed on MaterialTransactions: Phase 0's material-permissions-v1.sql already
-- granted SELECT, INSERT (with UPDATE/DELETE denied) at the table level, and the Reservation/
-- ReservationRelease transaction types written by this phase use that same existing grant.
