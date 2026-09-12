-- Audit log immutability.
--
-- The same four-layer treatment the inventory ledger gets, for the same reason:
-- a record of who did what is only worth keeping if it cannot be quietly
-- rewritten by whoever did it.
--
-- Layer 1 is the domain type, which has no setters. Layer 2 is the EF
-- SaveChanges interceptor. This is layer 3, and it catches anything arriving by
-- raw SQL. Layer 4 is the application database role, granted only SELECT and
-- INSERT on this table.

DROP TRIGGER IF EXISTS trg_audit_log_immutable ON audit.audit_log;

CREATE TRIGGER trg_audit_log_immutable
    BEFORE UPDATE OR DELETE ON audit.audit_log
    FOR EACH ROW
    EXECUTE FUNCTION inventory.deny_mutation();

-- Truncate bypasses row-level triggers entirely, so it is blocked separately.
DROP TRIGGER IF EXISTS trg_audit_log_no_truncate ON audit.audit_log;

CREATE TRIGGER trg_audit_log_no_truncate
    BEFORE TRUNCATE ON audit.audit_log
    FOR EACH STATEMENT
    EXECUTE FUNCTION inventory.deny_mutation();

-- Sign-in attempts are evidence too: the record of a brute-force attempt is
-- worth as much as the record of a successful entry, and an attacker who can
-- delete their failures can search a password space unobserved.
DROP TRIGGER IF EXISTS trg_login_attempt_immutable ON core.login_attempt;

CREATE TRIGGER trg_login_attempt_immutable
    BEFORE UPDATE OR DELETE ON core.login_attempt
    FOR EACH ROW
    EXECUTE FUNCTION inventory.deny_mutation();

-- Retention is handled by dropping whole partitions as they age out, which is a
-- deliberate, scheduled, auditable act rather than a DELETE anyone can issue.
