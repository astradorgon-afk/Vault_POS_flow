-- Balance guard, deferred to commit.
--
-- Supersedes the trigger installed by 02_balance_guard.sql. Two things changed:
-- when it runs, and how it decides which movements account for a change.
--
-- Why the original fired too early
-- -------------------------------
-- It ran per statement, and so required movements for a bucket to be inserted
-- before the balance row summarising them. Nothing guarantees that order:
-- Entity Framework decides insert order from its own dependency graph, and
-- inventory_balance has no foreign key to inventory_movement, so it is free to
-- write balances first. When it did, the guard found no backing movements and
-- rejected a perfectly legitimate posting.
--
-- A DEFERRABLE INITIALLY DEFERRED constraint trigger runs at COMMIT, by which
-- point every row the transaction intends to write is present. Statement order
-- stops mattering, and the check gets stronger: it sees the transaction's final
-- state rather than a moment part-way through it.
--
-- Why the transaction filter had to go
-- ------------------------------------
-- The original also matched movements on xmin = the current transaction id.
-- That looked tighter and was in fact broken: Entity Framework takes a savepoint
-- around each SaveChanges when one is already inside an explicit transaction, so
-- rows written there carry the SUBtransaction's id, while pg_current_xact_id()
-- returns the top-level one. Two postings in a single transaction - a sale and
-- its correction, say - silently matched nothing and aborted at commit.
--
-- The identifier window below is the real check and does not need that filter.
-- Movements are append-only and their identifiers are UUIDv7, so they sort in
-- creation order; the window (OLD.last_movement_id, NEW.last_movement_id] names
-- exactly the legs this change claims to account for, whoever wrote them. A bare
--     UPDATE inventory.inventory_balance SET quantity = 100 WHERE ...
-- leaves last_movement_id untouched, so the window is empty, the expected delta
-- is zero, and the change is rejected. That is the statement this whole design
-- exists to make impossible.
--
-- Cost is proportional to what the transaction just wrote, not to the bucket's
-- history. Whole-bucket agreement between ledger and projection is verified
-- separately, by the nightly reconciliation job.

CREATE OR REPLACE FUNCTION inventory.balance_matches_ledger()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    v_expected      numeric(18,3);
    v_actual        numeric(18,3);
    v_lower_bound   uuid;
BEGIN
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
       AND m.id > v_lower_bound
       AND m.id <= NEW.last_movement_id;

    IF v_expected <> v_actual THEN
        RAISE EXCEPTION
            'Inventory balance change of % for (location %, product %, batch %, state %) is not backed by movements in the ledger (found %). Stock may only change through the inventory ledger.',
            v_actual, NEW.location_id, NEW.product_id, NEW.batch_key, NEW.state, v_expected
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    RETURN NULL;
END;
$$;

-- The immediate trigger goes; the deferred constraint trigger replaces it.
DROP TRIGGER IF EXISTS trg_inventory_balance_guard ON inventory.inventory_balance;

CREATE CONSTRAINT TRIGGER trg_inventory_balance_guard
    AFTER INSERT OR UPDATE ON inventory.inventory_balance
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
    EXECUTE FUNCTION inventory.balance_matches_ledger();
