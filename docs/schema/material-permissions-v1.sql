-- MaterialLedger — least-privilege grants for the `materialledger_app` login, Phase 0.
--
-- WHY THIS FILE EXISTS
-- ---------------------
-- These GRANT/DENY statements were applied live against forge-sql via sqlcmd as part of Task 5
-- (see .superpowers/sdd/2026-08-07-material-ledger-phase0/progress.md and the Phase 0 plan,
-- "Task 5: Apply the migration to forge and grant least-privilege permissions") but were never
-- committed to this repo. That meant rebuilding the database from the EF Core migration alone
-- (docs/schema/material-schema-v1.sql or `dotnet ef database update`) would silently recreate the
-- schema WITHOUT the append-only guarantee on material.MaterialTransactions — the DENY UPDATE,
-- DELETE below is what makes MaterialTransactions genuinely append-only even for the application's
-- own SQL login, not just "append-only by convention in application code."
--
-- WHEN TO RUN THIS
-- ----------------
-- An administrator (using an account with permission to grant/deny, e.g. sa or a member of
-- db_owner/db_securityadmin) must re-run this script after any rebuild of the MaterialLedger
-- database from scratch (fresh `dotnet ef database update`, restore from a schema-only backup,
-- etc.). GRANT and DENY are naturally idempotent in SQL Server — re-running this script against a
-- database that already has these exact permissions is a safe no-op.
--
-- SCOPE / WHAT `materialledger_app` DOES NOT HAVE
-- ------------------------------------------------
-- `materialledger_app` intentionally has NO DDL rights (no CREATE/ALTER/DROP TABLE, no ability to
-- change schema) and NO grants on any table beyond the three below. Every other table in the
-- `material` schema belongs to a later Phase's write surface and must only be granted to
-- `materialledger_app` in the plan that introduces the stored procedure/code path that needs it —
-- grants arrive with the code that uses them, not all at once. Schema changes are applied by a
-- human running `dotnet ef database update` (or this repo's generated migration script) using the
-- admin connection string, never by the running application.
--
-- Run against the real `MaterialLedger` database (not the throwaway `MaterialLedgerTest` database
-- used by the integration test suite).

USE MaterialLedger;
GO

-- AppUsers: the app reads users to authenticate, and may need to insert/update accounts later
-- (e.g. password resets) — no DELETE, so accounts are deactivated (IsActive = 0) rather than
-- removed.
GRANT SELECT, INSERT, UPDATE ON material.AppUsers TO materialledger_app;

-- OperationLogs: the idempotency guard's log table. Full CRUD is intentional here — unlike
-- MaterialTransactions, log rows are operational bookkeeping, not the financial ledger itself.
GRANT SELECT, INSERT, UPDATE, DELETE ON material.OperationLogs TO materialledger_app;

-- MaterialTransactions is append-only at the DB level, even for the app login: the app may SELECT
-- and INSERT new transaction rows, but is explicitly denied UPDATE/DELETE. DENY takes precedence
-- over any GRANT (including future GRANTs from broader role membership), so this holds even if
-- materialledger_app is later added to a role that would otherwise permit UPDATE/DELETE.
GRANT SELECT, INSERT ON material.MaterialTransactions TO materialledger_app;
DENY UPDATE, DELETE ON material.MaterialTransactions TO materialledger_app;
GO
