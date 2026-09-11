-- Applied after migrations, and safe to re-apply.
--
-- This is the fourth and outermost guard on the ledger: even a total compromise
-- of the application, or a successful SQL injection, cannot rewrite history,
-- because the role it connects as has no UPDATE or DELETE on these tables.

GRANT USAGE ON SCHEMA core, inventory, audit, sync TO pos_app;
GRANT USAGE ON SCHEMA core, inventory, audit, sync TO pos_readonly;

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA core      TO pos_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA inventory TO pos_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA sync      TO pos_app;

-- Append-only. No UPDATE. No DELETE. No TRUNCATE.
REVOKE UPDATE, DELETE, TRUNCATE ON inventory.inventory_movement FROM pos_app;
GRANT  SELECT, INSERT            ON inventory.inventory_movement TO   pos_app;

GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA core, inventory, sync TO pos_app;

GRANT SELECT ON ALL TABLES IN SCHEMA core, inventory, audit, sync TO pos_readonly;

-- Future tables inherit the same defaults, so a new migration cannot
-- accidentally hand out broader rights.
ALTER DEFAULT PRIVILEGES IN SCHEMA core, inventory, sync
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO pos_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA core, inventory, audit, sync
    GRANT SELECT ON TABLES TO pos_readonly;
