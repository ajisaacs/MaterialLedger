-- docs/schema/material-permissions-v10.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, vendor and
-- vendor-part reference data.
--
-- Grants the reads/inserts/updates VendorService requires. Vendor and VendorPart are mutable
-- reference entities; deleting either is intentionally not granted or exposed by the application.
--
-- Run against the real `MaterialLedger` database only after the AddVendorsAndVendorParts EF
-- migration has been applied, using an account with GRANT rights. This is a manual administrative
-- step; the application never applies its own schema or permission changes. The GRANT statements
-- are idempotent.

USE MaterialLedger;
GO

GRANT SELECT, INSERT, UPDATE ON material.Vendors TO materialledger_app;
GRANT SELECT, INSERT, UPDATE ON material.VendorParts TO materialledger_app;
GO
