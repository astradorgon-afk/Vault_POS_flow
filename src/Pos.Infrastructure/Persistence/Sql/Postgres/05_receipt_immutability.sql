-- Payment receipt immutability (ADR-0026).
--
-- A receipt is the printed proof that a cash event happened. Once it has been
-- handed to a customer or filed against an expense, the stored row must match
-- the paper forever, so receipts get the same four guards as the ledger and the
-- audit log: a domain type with no setters, the EF append-only interceptor,
-- these triggers, and a database role granted only SELECT and INSERT.

DROP TRIGGER IF EXISTS trg_receipt_immutable ON core.receipt;

CREATE TRIGGER trg_receipt_immutable
    BEFORE UPDATE OR DELETE ON core.receipt
    FOR EACH ROW
    EXECUTE FUNCTION inventory.deny_mutation();

-- Truncate bypasses row-level triggers entirely, so it is blocked separately.
DROP TRIGGER IF EXISTS trg_receipt_no_truncate ON core.receipt;

CREATE TRIGGER trg_receipt_no_truncate
    BEFORE TRUNCATE ON core.receipt
    FOR EACH STATEMENT
    EXECUTE FUNCTION inventory.deny_mutation();
