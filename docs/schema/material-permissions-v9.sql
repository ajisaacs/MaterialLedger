-- docs/schema/material-permissions-v9.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, purchase order
-- location preallocation and bulk receive.
--
-- Grants the reads/inserts/deletes PurchaseOrderService's AddLocationAllocationAsync and
-- RemoveLocationAllocationAsync need. DELETE is new here — no prior grant file has needed it,
-- since every other write path in this app is insert/update-only (cancel-by-status-flip, not
-- delete). Allocations are the first genuinely deletable row in this schema.
--
-- Run against the real `MaterialLedger` database only after the AddPurchaseOrderLineAllocations
-- EF migration has been applied, using an account with GRANT rights. This is a manual
-- administrative step; the application never applies its own schema or permission changes.

USE MaterialLedger;
GO

GRANT SELECT, INSERT, DELETE ON material.PurchaseOrderLineAllocations TO materialledger_app;
GO
