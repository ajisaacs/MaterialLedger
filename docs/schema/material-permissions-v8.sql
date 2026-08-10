-- docs/schema/material-permissions-v8.sql
-- MaterialLedger — least-privilege grants for the `materialledger_app` login, purchase order
-- entry and receiving-against-PO.
--
-- Grants the reads/inserts/updates PurchaseOrderService and the ReceiveMaterialService PO-line
-- extension need, plus a first-time grant for material.Attachments (the table has existed since
-- v1 but has never been granted — this is the first feature to actually write to it).
--
-- Run against the real `MaterialLedger` database only after the AddPurchaseOrders EF migration
-- has been applied, using an account with GRANT rights. This is a manual administrative step; the
-- application never applies its own schema or permission changes.

USE MaterialLedger;
GO

GRANT SELECT, INSERT, UPDATE ON material.PurchaseOrders TO materialledger_app;
GRANT SELECT, INSERT, UPDATE ON material.PurchaseOrderLines TO materialledger_app;
GRANT SELECT, INSERT ON material.Attachments TO materialledger_app;
GO
