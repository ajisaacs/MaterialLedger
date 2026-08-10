-- docs/schema/material-permissions-v6.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, multiple expected drops.
--
-- CreateReservationAsync records one row per operator-estimated usable drop in
-- material.ExpectedRemnants. This file only grants the app the reads/inserts needed for that new
-- provenance table. It is safe to re-run (GRANT is idempotent).
--
-- Run against the real `MaterialLedger` database only after the AddExpectedRemnants EF migration
-- has been applied, using an account with GRANT rights. This is a manual administrative step; the
-- application never applies its own schema or permission changes.

USE MaterialLedger;
GO

GRANT SELECT, INSERT ON material.ExpectedRemnants TO materialledger_app;
GO
