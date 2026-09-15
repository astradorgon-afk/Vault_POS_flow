-- Applied after migrations, as the owning role, and safe to re-apply.
--
-- This is the fourth and outermost guard on the permanent records: even a total
-- compromise of the application, or a successful SQL injection, cannot rewrite
-- history, because the role it connects as has no UPDATE or DELETE on them.
--
-- Plain SQL only (no psql meta-commands), so the deployment step and the
-- PostgreSQL test suite run exactly the same file.

-- Every schema the migrations create. A schema listed here that does not exist
-- yet (sync arrives with Phase 13) is skipped rather than failing the step.
DO $$
DECLARE
    schema_name text;
BEGIN
    FOREACH schema_name IN ARRAY ARRAY[
        'core', 'catalog', 'inventory', 'purchasing', 'transfers', 'quarantine', 'audit', 'sync', 'sales']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = schema_name) THEN
            CONTINUE;
        END IF;

        EXECUTE format('GRANT USAGE ON SCHEMA %I TO pos_app, pos_readonly', schema_name);

        -- Reporting reads everything and changes nothing.
        EXECUTE format('GRANT SELECT ON ALL TABLES IN SCHEMA %I TO pos_readonly', schema_name);
        EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT ON TABLES TO pos_readonly', schema_name);

        -- The audit schema holds only append-only tables, granted one by one below.
        IF schema_name <> 'audit' THEN
            EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO pos_app', schema_name);
            EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO pos_app', schema_name);

            -- Future tables inherit the same rights, so a new migration cannot
            -- accidentally hand out broader ones (or none at all).
            EXECUTE format(
                'ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO pos_app',
                schema_name);
            EXECUTE format(
                'ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT USAGE, SELECT ON SEQUENCES TO pos_app',
                schema_name);
        END IF;
    END LOOP;
END
$$;

-- Append-only records. No UPDATE. No DELETE. No TRUNCATE.
-- This is the guard that survives a compromised application: the role it
-- connects as simply has no statement available to rewrite history.
REVOKE UPDATE, DELETE, TRUNCATE ON inventory.inventory_movement FROM pos_app;
GRANT  SELECT, INSERT           ON inventory.inventory_movement TO   pos_app;

REVOKE UPDATE, DELETE, TRUNCATE ON audit.audit_log              FROM pos_app;
GRANT  SELECT, INSERT           ON audit.audit_log              TO   pos_app;

REVOKE UPDATE, DELETE, TRUNCATE ON core.login_attempt           FROM pos_app;
GRANT  SELECT, INSERT           ON core.login_attempt           TO   pos_app;

-- A payment receipt is the printed proof of a cash event (ADR-0026).
REVOKE UPDATE, DELETE, TRUNCATE ON core.receipt                 FROM pos_app;
GRANT  SELECT, INSERT           ON core.receipt                 TO   pos_app;

-- A refused stock draw is the only trace of stock sold or shipped that the
-- counters say is not there; a shrinkage signal must not be erasable.
REVOKE UPDATE, DELETE, TRUNCATE ON inventory.negative_stock_attempt FROM pos_app;
GRANT  SELECT, INSERT           ON inventory.negative_stock_attempt TO   pos_app;

-- Return inspection decisions are permanent inventory history.
REVOKE UPDATE, DELETE, TRUNCATE ON sales.sales_return_disposition FROM pos_app;
GRANT SELECT, INSERT ON sales.sales_return_disposition TO pos_app;
