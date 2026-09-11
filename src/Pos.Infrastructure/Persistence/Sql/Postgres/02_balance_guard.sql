-- Balance guard.
--
-- This is the trigger that makes `product.StockQuantity = 100` impossible even
-- for code that bypasses the application entirely.
--
-- Every change to a balance row must be matched, exactly, by inventory movements
-- inserted in the SAME transaction for the SAME bucket. A bare
--     UPDATE inventory.inventory_balance SET quantity = 100 WHERE ...
-- has no accompanying movements and is rejected.
--
-- Scope of the check: only the movements inserted by the current transaction and
-- attributable to this particular update are summed, identified by
--   * xmin equal to the current transaction id, and
--   * an identifier in (OLD.last_movement_id, NEW.last_movement_id].
-- Identifiers are UUIDv7, so they sort in creation order and the window is
-- unambiguous. This keeps the check exact while touching only the handful of
-- rows the transaction just wrote, rather than re-summing a bucket's entire
-- history on every sale. Whole-bucket agreement between the ledger and the
-- projection is verified separately by the nightly reconciliation job.

CREATE OR REPLACE FUNCTION inventory.balance_matches_ledger()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    v_current_xid   bigint;
    v_expected      numeric(18,3);
    v_actual        numeric(18,3);
    v_lower_bound   uuid;
BEGIN
    v_current_xid := (pg_current_xact_id()::text::bigint % 4294967296);

    IF TG_OP = 'UPDATE' THEN
        v_actual := NEW.quantity - OLD.quantity;
        v_lower_bound := OLD.last_movement_id;
    ELSE
        v_actual := NEW.quantity;
        v_lower_bound := '00000000-0000-0000-0000-000000000000'::uuid;
    END IF;

    SELECT COALESCE(SUM(m.quantity_delta), 0)
      INTO v_expected
      FROM inventory.inventory_movement m
     WHERE m.location_id = NEW.location_id
       AND m.product_id  = NEW.product_id
       AND COALESCE(m.batch_id, '00000000-0000-0000-0000-000000000000'::uuid) = NEW.batch_key
       AND m.state       = NEW.state
       AND m.xmin::text::bigint = v_current_xid
       AND m.id > v_lower_bound
       AND m.id <= NEW.last_movement_id;

    IF v_expected <> v_actual THEN
        RAISE EXCEPTION
            'Inventory balance change of % for (location %, product %, batch %, state %) is not backed by movements in this transaction (found %). Stock may only change through the inventory ledger.',
            v_actual, NEW.location_id, NEW.product_id, NEW.batch_key, NEW.state, v_expected
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_inventory_balance_guard ON inventory.inventory_balance;

CREATE TRIGGER trg_inventory_balance_guard
    BEFORE INSERT OR UPDATE ON inventory.inventory_balance
    FOR EACH ROW
    EXECUTE FUNCTION inventory.balance_matches_ledger();

-- A deleted balance row would silently discard stock. Rebuilding the projection
-- is done by the maintenance role, which disables this trigger explicitly.
CREATE OR REPLACE FUNCTION inventory.deny_balance_delete()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION
        'Inventory balances are not deletable. Post a movement that brings the bucket to zero instead.'
        USING ERRCODE = 'restrict_violation';
END;
$$;

DROP TRIGGER IF EXISTS trg_inventory_balance_no_delete ON inventory.inventory_balance;

CREATE TRIGGER trg_inventory_balance_no_delete
    BEFORE DELETE ON inventory.inventory_balance
    FOR EACH ROW
    EXECUTE FUNCTION inventory.deny_balance_delete();
