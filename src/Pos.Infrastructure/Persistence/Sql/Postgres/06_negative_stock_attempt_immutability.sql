-- Negative-stock attempt immutability.
--
-- A refused draw leaves no trace in the ledger, so this table is the only
-- evidence that stock the counters say is missing was sold or shipped anyway.
-- It gets the same four guards as the ledger, the audit log and receipts: a
-- domain type with no setters, the EF append-only interceptor, these triggers,
-- and a database role granted only SELECT and INSERT.

DROP TRIGGER IF EXISTS trg_negative_stock_attempt_immutable ON inventory.negative_stock_attempt;

CREATE TRIGGER trg_negative_stock_attempt_immutable
    BEFORE UPDATE OR DELETE ON inventory.negative_stock_attempt
    FOR EACH ROW
    EXECUTE FUNCTION inventory.deny_mutation();

-- Truncate bypasses row-level triggers entirely, so it is blocked separately.
DROP TRIGGER IF EXISTS trg_negative_stock_attempt_no_truncate ON inventory.negative_stock_attempt;

CREATE TRIGGER trg_negative_stock_attempt_no_truncate
    BEFORE TRUNCATE ON inventory.negative_stock_attempt
    FOR EACH STATEMENT
    EXECUTE FUNCTION inventory.deny_mutation();
