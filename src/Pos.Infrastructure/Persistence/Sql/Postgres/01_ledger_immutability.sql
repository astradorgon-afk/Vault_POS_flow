-- Ledger immutability.
--
-- Layer 3 of 4. The domain type has no setters, an EF interceptor rejects
-- modified and deleted entries, this trigger rejects anything that reaches the
-- database by another route (raw SQL, a migration, a console), and the
-- application's database role is granted only SELECT and INSERT on this table.
--
-- Corrections are made by appending a reversing movement group, never by
-- editing history.

CREATE OR REPLACE FUNCTION inventory.deny_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION
        'Table %.% is append-only; % is not permitted. Post a reversing entry instead.',
        TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
        USING ERRCODE = 'restrict_violation';
END;
$$;

DROP TRIGGER IF EXISTS trg_inventory_movement_immutable ON inventory.inventory_movement;

CREATE TRIGGER trg_inventory_movement_immutable
    BEFORE UPDATE OR DELETE ON inventory.inventory_movement
    FOR EACH ROW
    EXECUTE FUNCTION inventory.deny_mutation();

-- Truncate would bypass the row-level trigger entirely.
DROP TRIGGER IF EXISTS trg_inventory_movement_no_truncate ON inventory.inventory_movement;

CREATE TRIGGER trg_inventory_movement_no_truncate
    BEFORE TRUNCATE ON inventory.inventory_movement
    FOR EACH STATEMENT
    EXECUTE FUNCTION inventory.deny_mutation();
