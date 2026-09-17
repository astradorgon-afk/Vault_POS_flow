# Database Design

PostgreSQL 17 is authoritative. SQLite is a per-device subset. This document
covers the server schema; §12 covers the differences on the client.

---

## 1. Conventions

| Concern | Rule |
|---|---|
| Naming | `snake_case` tables and columns; tables singular (`inventory_movement`) |
| Primary keys | `uuid` (UUIDv7, time-ordered) named `id` |
| Money | `numeric(19,4)`; currency held on the owning row or inherited from org |
| Quantity | `numeric(18,3)` |
| Conversion factors | `numeric(18,6)` |
| Rates / percentages | `numeric(9,4)` |
| Timestamps | `timestamptz`, always UTC, suffixed `_at_utc` |
| Business dates | `date`, computed in the location's timezone, suffixed `_date` |
| Enums | `smallint` columns backed by C# enums, with a `CHECK` on the valid range and a lookup view for reporting |
| Booleans | `boolean`, non-nullable, explicit default |
| Text | `text` with `CHECK (length(...) <= n)` rather than `varchar(n)` |
| Concurrency | system column `xmin` mapped as the EF concurrency token |
| Soft state | `is_active` + `deactivated_at_utc`; **no soft-delete flag on ledger/financial rows** |
| Deletes | `ON DELETE RESTRICT` everywhere except child collections owned by an aggregate (`CASCADE` only for lines of an unposted draft) |

Schemas: `core` (org, locations, identity, devices), `catalog`, `inventory`,
`purchasing`, `transfers`, `sales`, `sync`, `audit`. Separate schemas keep
permission grants meaningful (§11).

---

## 2. Core

### `core.organization`
`id, name, legal_name, currency_code, default_time_zone_id, tax_settings_json,
cash_rounding_increment numeric(19,4), created_at_utc, xmin`

### `core.location`
```
id                    uuid PK
organization_id       uuid FK -> organization RESTRICT
code                  text UNIQUE      -- 'MAIN', 'STORE1', 'EXT-SUPPLIER'
name                  text
kind                  smallint         -- 0 MainWarehouse, 1 Store, 2 External
time_zone_id          text             -- IANA, e.g. 'Asia/Manila'
address_json          jsonb
is_active             boolean
opened_on             date
closed_on             date NULL
settings_json         jsonb            -- negative stock policy, thresholds, receipt text
created_at_utc        timestamptz
xmin
```
Constraints
- `UNIQUE (organization_id, code)`
- partial unique index: at most one active main warehouse
  `CREATE UNIQUE INDEX ux_location_single_main ON core.location (organization_id)
   WHERE kind = 0 AND is_active;`
- `CHECK (kind BETWEEN 0 AND 2)`

### `core.receipt`
Standalone payment receipts (ADR-0026). A record that a cash event happened, not
a balance: no ledger rows and no child tables.
```
id                    uuid PK
number                varchar(20)      -- 'RCT-2026-000001'
kind                  smallint         -- 1 WalkInSale, 2 BranchExpense, 3 OwnerWithdrawal
location_id           uuid             -- the branch; never an External counterparty
amount                numeric(19,4)    -- > 0, enforced by the domain and validator
counterparty          varchar(128) NULL
note                  varchar(512) NULL
reference_number      varchar(24) NULL -- printed document number, not a foreign key
issued_by_user_id     uuid
issued_at_utc         timestamptz
```
Indexes
- `ux_receipt_number` UNIQUE (`number`)
- `ix_receipt_location_time` (`location_id`, `issued_at_utc`)

Migration `20260913112917_AddReceipts`. Grants come from the `core` schema's
default privileges; no table-specific grant is needed.

### Identity (ASP.NET Core Identity, customised)
`core.app_user`, `core.app_role`, `core.app_user_role`, `core.app_user_claim`,
`core.app_role_claim`, `core.app_user_login`, `core.app_user_token`.

Additional tables:
```
core.permission              (code PK text, module text, description text, is_offline_capable boolean)
core.role_permission         (role_id, permission_code) PK both
core.user_permission_override(user_id, permission_code, effect smallint, expires_at_utc, granted_by, reason) 
core.user_location           (user_id, location_id, is_primary) PK both
core.refresh_token           (id, user_id, device_id, token_hash bytea UNIQUE,
                              issued_at_utc, expires_at_utc, revoked_at_utc,
                              replaced_by_id, reuse_detected boolean, ip inet)
core.device                  (see DOMAIN_MODEL §4)
core.device_session          (id, device_id, user_id, started_at_utc, ended_at_utc,
                              ip inet, app_version, ended_reason smallint)
core.login_attempt           (id, user_name_hash, ip inet, succeeded, at_utc, failure_reason)
```
Indexes: `refresh_token(token_hash)`, `refresh_token(user_id, expires_at_utc)`,
`login_attempt(ip, at_utc DESC)`, `login_attempt(user_name_hash, at_utc DESC)`.

---

## 3. Catalog

```
catalog.product_category (id, parent_id NULL FK self RESTRICT, name, code UNIQUE, sort_order, is_active)
catalog.brand            (id, name UNIQUE, is_active)
catalog.unit_of_measure  (id, code UNIQUE, name, kind smallint /*Count|Weight|Volume*/, decimal_places smallint)
catalog.supplier         (id, code UNIQUE, name, tax_id, contact_json, payment_terms_days,
                          lead_time_days, is_active, rating numeric(4,2))

catalog.product
  id, sku text UNIQUE, name, description,
  category_id FK, brand_id FK NULL, primary_supplier_id FK NULL,
  base_uom_id FK RESTRICT,
  tax_code text, is_vat_exempt boolean,
  default_purchase_cost numeric(19,4),
  tracks_batches boolean, tracks_expiry boolean, shelf_life_days int NULL,
  image_ref text NULL,
  is_active boolean, discontinued_on date NULL,
  created_at_utc, updated_at_utc, created_by, updated_by, xmin

catalog.product_barcode
  id, product_id FK CASCADE-on-aggregate, barcode text, symbology smallint,
  uom_id FK, pack_quantity numeric(18,3), is_primary boolean,
  created_at_utc, created_by,
  retired_at_utc NULL, retired_by NULL     -- retired codes stop scanning (ADR-0029)
  UNIQUE (barcode)                         -- globally unique across the catalog, retired codes included
  UNIQUE (product_id) WHERE is_primary     -- at most one primary; demotions are written first on a swap

catalog.product_unit_conversion
  id, product_id FK, from_uom_id, to_uom_id, factor numeric(18,6) CHECK (factor > 0)
  UNIQUE (product_id, from_uom_id, to_uom_id)

catalog.product_supplier
  (product_id, supplier_id) PK, supplier_sku, last_cost numeric(19,4),
  lead_time_days, minimum_order_quantity numeric(18,3), is_preferred boolean

catalog.product_price
  id, product_id FK, location_id FK NULL /*NULL = all locations*/,
  price numeric(19,4), effective_from_utc, effective_to_utc NULL,
  created_by, created_at_utc, reason text
  EXCLUDE USING gist (product_id WITH =, coalesce(location_id, uuid_nil()) WITH =,
                      tstzrange(effective_from_utc, effective_to_utc) WITH &&)
      -- no overlapping price periods for the same product+scope; a new price
      -- closes its predecessor's end rather than overlapping it (ADR-0029)

catalog.product_location_setting
  (product_id, location_id) PK,
  is_stocked boolean,
  minimum_stock, reorder_point, target_stock, maximum_stock,
  preferred_replenishment_quantity   -- all numeric(18,3)
  CHECK (minimum_stock <= reorder_point AND reorder_point <= target_stock
         AND target_stock <= maximum_stock)
```

Indexes: `product(sku)`, `product(category_id) WHERE is_active`,
`product_barcode(barcode)` (unique, the POS hot path),
GIN trigram index on `product(name)` for fast search-as-you-type,
`product_price(product_id, location_id, effective_from_utc DESC)`.

---

## 4. Inventory

### `inventory.inventory_movement` — the ledger

```
id                       uuid PK
event_id                 uuid NOT NULL
movement_group_id        uuid NOT NULL
leg_number               smallint NOT NULL CHECK (leg_number >= 1)
product_id               uuid FK RESTRICT
batch_id                 uuid FK RESTRICT NULL
location_id              uuid FK RESTRICT
state                    smallint NOT NULL
quantity_delta           numeric(18,3) NOT NULL CHECK (quantity_delta <> 0)
unit_cost                numeric(19,4) NOT NULL CHECK (unit_cost >= 0)
total_value_delta        numeric(19,4) NOT NULL
movement_type            smallint NOT NULL
source_location_id       uuid NULL
destination_location_id  uuid NULL
source_state             smallint NULL
destination_state        smallint NULL
reference_document_type  smallint NOT NULL
reference_document_id    uuid NULL
reference_number         text NOT NULL
created_by_user_id       uuid FK RESTRICT
approved_by_user_id      uuid FK RESTRICT NULL
device_id                uuid FK RESTRICT NULL
occurred_at_utc          timestamptz NOT NULL
recorded_at_utc          timestamptz NOT NULL DEFAULT now()
business_date            date NOT NULL
reason_code              smallint NULL
notes                    text NULL
reverses_movement_group_id uuid NULL
sync_status              smallint NOT NULL
server_processing_status smallint NOT NULL
correlation_id           uuid NOT NULL
change_sequence          bigint NOT NULL DEFAULT nextval('sync.change_sequence')
```

Constraints and indexes
- `UNIQUE (movement_group_id, leg_number)`
- `CHECK (total_value_delta = round(unit_cost * quantity_delta, 4))`
- `CHECK (state <> 9 /*External*/ OR location_id IN (SELECT ...))` — enforced by
  trigger since a CHECK cannot subquery: external state only on external locations.
- batch rule trigger: `tracks_batches = true ⇒ batch_id IS NOT NULL`
- `idx_movement_bucket (location_id, product_id, batch_id, state, recorded_at_utc DESC)`
- `idx_movement_product_time (product_id, recorded_at_utc DESC)`
- `idx_movement_reference (reference_document_type, reference_document_id)`
- `idx_movement_group (movement_group_id)`
- `idx_movement_event (event_id)`
- `idx_movement_change_seq (change_sequence)`
- **Partitioning**: none in v1 (ADR-0030). The ledger is never pruned, so
  partitions would buy no detach/drop, while PostgreSQL would force the partition
  key into the primary key and the leg-number unique index. Revisit at about 50
  million rows or with an archive policy that snapshots opening balances.

Triggers
```sql
CREATE TRIGGER trg_movement_immutable
BEFORE UPDATE OR DELETE ON inventory.inventory_movement
FOR EACH ROW EXECUTE FUNCTION inventory.deny_mutation();
```

### `inventory.inventory_balance` — projection

```
location_id  uuid, product_id uuid,
batch_key    uuid NOT NULL,         -- batch_id, or uuid_nil() when not batch-tracked
state        smallint,
quantity            numeric(18,3) NOT NULL DEFAULT 0,
average_unit_cost   numeric(19,4) NOT NULL DEFAULT 0,
total_value         numeric(19,4) NOT NULL DEFAULT 0,
last_movement_id    uuid NOT NULL,
last_movement_at_utc timestamptz NOT NULL,
xmin
PRIMARY KEY (location_id, product_id, batch_key, state)
```
- `idx_balance_lookup (product_id, location_id) INCLUDE (quantity) WHERE state = 0`
- `idx_balance_low_stock (location_id, product_id) WHERE state = 0`
- Trigger `trg_balance_matches_ledger` verifies each update's delta against the
  movements inserted in the same transaction; mismatch aborts.

### Other inventory tables
```
inventory.batch                  (see DOMAIN_MODEL §6.1)
  UNIQUE (product_id, lot_number, supplier_id)
  idx_batch_fefo (product_id, expires_on) WHERE status = 0

inventory.inventory_reservation  (id, sale_id/transfer_id, product_id, batch_id,
                                  location_id, quantity, expires_at_utc, status)
inventory.inventory_count        (id, number CNT UNIQUE, location_id, kind, status, note,
                                  snapshot_taken_at_utc, created_by/at, submitted_by/at,
                                  approved_by, posted_at_utc, last_rejection_reason,
                                  cancellation_reason)
  inventory_count_line            (id, inventory_count_id FK CASCADE, line_no, product_id,
                                  batch_id, system_quantity, physical_quantity NULL,
                                  unit_cost, counted_by/at, is_repeat_variance)
                                  UNIQUE (inventory_count_id, line_no)
inventory.stock_adjustment       (id, number ADJ UNIQUE WHERE number <> '', location_id,
                                  reason, notes, status, created_by/at, submitted_at,
                                  decided_by/at, rejection_reason, reversed_by/at,
                                  reversal_reason)
  stock_adjustment_line           (id, stock_adjustment_id FK CASCADE, line_no, product_id,
                                  batch_id, state, quantity_delta, unit_cost, movement_type)
                                  UNIQUE (stock_adjustment_id, line_no)
                                 -- documents only; stock changes solely through the
                                 -- movements they post on approval (ADR-0031)
inventory.quarantine_incident    + _line + _photo
inventory.integrity_incident     (id, kind, detected_at_utc, details_json, status)
inventory.negative_stock_attempt (id, event_id, movement_type, location_id, product_id,
                                  batch_key, state, requested_quantity, available_quantity,
                                  policy, reference_document_type/id, reference_number,
                                  user_id, device_id, correlation_id, attempted_at_utc)
                                 -- append-only; written after the refused command
                                 -- rolls back (ADR-0030)
```

---

## 5. Purchasing

```
purchasing.purchase_order
  id, number text UNIQUE, supplier_id FK, destination_location_id FK,
  status smallint, currency_code,
  ordered_at_utc NULL, expected_at_utc NULL,
  subtotal, tax_total, grand_total numeric(19,4),
  created_by, created_at_utc, cancelled_reason, closed_reason, xmin

purchasing.purchase_order_line
  id, purchase_order_id FK CASCADE, line_no, product_id FK RESTRICT,
  uom_id, ordered_quantity numeric(18,3) CHECK (> 0),
  unit_cost numeric(19,4), tax_code, line_total numeric(19,4)
  UNIQUE (purchase_order_id, line_no)

purchasing.purchase_approval
  id, purchase_order_id FK, approver_user_id, decision smallint,
  decided_at_utc, notes, threshold_applied numeric(19,4)

purchasing.goods_receipt
  id, number text UNIQUE, purchase_order_id FK NULL, supplier_id FK,
  location_id FK, received_at_utc, received_by, verified_by NULL,
  status smallint, is_direct_to_store boolean, authorization_ref text NULL

purchasing.goods_receipt_line
  id, goods_receipt_id FK CASCADE, purchase_order_line_id FK NULL,
  product_id FK NULL,               -- NULL when the barcode was unknown
  raw_barcode text NULL,
  quantity_expected, quantity_received, quantity_rejected numeric(18,3),
  unit_cost numeric(19,4), batch_id FK NULL, expires_on date NULL,
  destination_state smallint         -- Available | PendingInspection | Quarantine

purchasing.receiving_discrepancy
  id, goods_receipt_line_id FK, kind smallint, quantity numeric(18,3),
  value_impact numeric(19,4), notes, status smallint,
  resolved_by NULL, resolved_at_utc NULL

purchasing.supplier_return + purchasing.supplier_return_line
```

---

## 6. Transfers

```
transfers.transfer_order
  id, number text UNIQUE, kind smallint, mode smallint, status smallint,
  source_location_id FK, destination_location_id FK
     CHECK (source_location_id <> destination_location_id),
  requested_by, requested_at_utc, required_by_date,
  priority smallint, notes, pre_approval_token_id uuid NULL,
  central_review_status smallint NULL, xmin

transfers.transfer_order_line
  id, transfer_order_id FK CASCADE, line_no, product_id FK,
  quantity_requested, quantity_approved, quantity_sent,
  quantity_received, quantity_damaged, quantity_missing numeric(18,3),
  unit_cost numeric(19,4)

transfers.transfer_approval    (id, transfer_order_id, approver_user_id, decision,
                                decided_at_utc, notes, modified_quantities_json)
transfers.transfer_shipment    (id, transfer_order_id, number, dispatched_at_utc,
                                dispatched_by, carrier, vehicle_ref, seal_number)
transfers.transfer_shipment_line
transfers.transfer_receipt     (id, transfer_order_id, shipment_id, number,
                                received_at_utc, received_by, verified_by,
                                verified_at_utc)
transfers.transfer_receipt_line
transfers.transfer_discrepancy (id, transfer_order_id, line_id, kind smallint,
                                quantity, value_impact, status, investigation_notes,
                                resolved_by, resolved_at_utc, resolution smallint)
transfers.transfer_custody_event
  id, transfer_order_id, event_kind smallint, user_id, device_id, location_id,
  at_utc, notes
transfers.pre_approval_token
  id, issued_to_location_id, kind smallint, scope_json, max_value numeric(19,4),
  issued_by, issued_at_utc, expires_at_utc, consumed_by_transfer_id NULL,
  signature bytea
```

**Phase 6 deviation (implemented, `20260912203959_AddTransfers`).** Shipment and
receipt details live **inline on `transfer_order`** (`shipment_id`,
`shipment_number`, `receipt_id`, `receipt_number`, plus dispatched/received/
verified actor + timestamp columns) rather than in separate `transfer_shipment`
/`transfer_receipt` tables; picked stock is a single `transfer_allocation` table
(`transfer_order_id`, `line_no`, `batch_id`, `quantity`, `unit_cost`,
`received_quantity`, `damaged_quantity`) instead of shipment/receipt line
tables; `transfer_discrepancy` carries `batch_id`, `resolution_outcome`,
`resolved_by/at`, `resolution_note`; custody events have a monotonic
`sequence` keys and `actor_user_id`. The `number` column is `text` with a
filtered unique index (`number <> ''`) shared by drafts. `transfer_approval`
and `pre_approval_token` are Phase 7 — the approvals table deferred (approver +
timestamp columns inline on `transfer_order`), the token table not yet created.

---

## 7. Sales

```
sales.cashier_shift
  id, number text UNIQUE, location_id, device_id, cashier_user_id,
  opened_at_utc, opening_float numeric(19,4),
  closed_at_utc NULL, declared_cash NULL, counted_cash NULL,
  cash_variance numeric(19,4) NULL, status smallint, business_date date

sales.sale
  id, number text UNIQUE, cashier_shift_id FK, location_id, device_id,
  customer_id FK NULL, status smallint,
  subtotal, discount_total, tax_total, grand_total numeric(19,4),
  occurred_at_utc, recorded_at_utc, business_date,
  event_id uuid UNIQUE,             -- idempotency key from the device
  is_offline_origin boolean, xmin

sales.sale_item
  id, sale_id FK CASCADE, line_no, product_id FK RESTRICT, batch_id FK NULL,
  quantity numeric(18,3), uom_id, unit_price, discount_amount,
  tax_code, tax_amount, line_total numeric(19,4),
  price_overridden_by uuid NULL, returned_quantity numeric(18,3) DEFAULT 0
  UNIQUE (sale_id, line_no)

sales.payment
  id, sale_id FK, method smallint, amount numeric(19,4),
  tendered numeric(19,4) NULL, change_given numeric(19,4) NULL,
  provider text NULL, provider_transaction_ref text NULL,
  payment_token text NULL, masked_last4 text NULL, status smallint,
  CHECK (method <> 0 /*Cash*/ OR provider IS NULL)

sales.customer      (id, display_name, phone NULL, email NULL, tin NULL, note NULL,
                     is_active, created_at_utc/by, updated_at_utc/by,
                     deactivated_at_utc/by NULL, deactivation_reason NULL)
sales.sales_return  + sales.sales_return_item
sales.receipt_print_log (id, sale_id, printed_at_utc, printed_by, is_reprint, reason)
```

Indexes: `sale(location_id, business_date)`, `sale(cashier_shift_id)`,
`sale(event_id)` unique, `sale_item(product_id, sale_id)`,
`sale(occurred_at_utc DESC)`. `sales.sale` is partitioned monthly by
`business_date` once volume warrants it (the migration is prepared but not
applied at install).

---

## 8. Sync

```
sync.change_sequence            SEQUENCE, shared by all feed-bearing tables

sync.change_log
  change_sequence bigint PK DEFAULT nextval('sync.change_sequence'),
  entity_type smallint, entity_id uuid, operation smallint,
  location_scope_id uuid NULL,     -- NULL = global (master data)
  payload jsonb, occurred_at_utc timestamptz
  idx_change_log_scope (location_scope_id, change_sequence)

sync.processed_event
  event_id uuid PK,
  device_id uuid, user_id uuid, event_type smallint,
  device_sequence bigint, payload_hash bytea,
  outcome smallint,                 -- Accepted|Duplicate|Rejected|RequiresReview|Conflict
  result_json jsonb,                -- the exact response replayed on retry
  processed_at_utc timestamptz,
  correlation_id uuid
  UNIQUE (device_id, device_sequence)

sync.sync_checkpoint
  device_id uuid PK,
  last_accepted_device_sequence bigint,
  last_delivered_change_sequence bigint,
  last_push_at_utc, last_pull_at_utc,
  master_data_version bigint, policy_version bigint

sync.sync_failure
  id, event_id, device_id, attempt_count, first_failed_at_utc, last_attempt_at_utc,
  next_retry_at_utc, error_code, error_message, server_response jsonb,
  correlation_id, status smallint
```

`processed_event` is the idempotency backbone. It is written in the **same
transaction** as the business effect; a crash between "effect committed" and
"idempotency recorded" is therefore impossible.

---

## 9. Audit and Notifications

```
audit.audit_log
  id uuid PK, change_sequence bigint,
  user_id, user_role_snapshot text, device_id NULL, location_id NULL,
  ip_address inet NULL, user_agent text NULL,
  action text NOT NULL, entity_type text NOT NULL, entity_id uuid NULL,
  previous_value jsonb NULL, new_value jsonb NULL,
  reason text NULL, reference_document_type smallint NULL,
  reference_document_id uuid NULL,
  occurred_at_utc timestamptz NOT NULL DEFAULT now(),
  correlation_id uuid NOT NULL
  -- PARTITION BY RANGE (occurred_at_utc) monthly
  -- immutability trigger identical to inventory_movement
  idx_audit_entity (entity_type, entity_id, occurred_at_utc DESC)
  idx_audit_user (user_id, occurred_at_utc DESC)
  idx_audit_correlation (correlation_id)

core.notification
  id uuid PK,
  kind smallint NOT NULL, severity smallint NOT NULL,
  title varchar(160) NOT NULL, body varchar(1000) NOT NULL,
  deduplication_key varchar(240) NOT NULL UNIQUE,
  location_id uuid NULL, product_id uuid NULL, batch_id uuid NULL,
  reference_document_type smallint NULL, reference_document_id uuid NULL,
  created_at_utc timestamptz NOT NULL,
  idx_notification_location_time (location_id, created_at_utc)

core.notification_receipt
  notification_id uuid FK -> notification ON DELETE CASCADE,
  user_id uuid,
  read_at_utc timestamptz NULL, acknowledged_at_utc timestamptz NULL,
  PRIMARY KEY (notification_id, user_id),
  idx_notification_receipt_user_read (user_id, read_at_utc)
```

---

## 10. Document numbering

```
core.document_counter
  document_type smallint,
  period_key    text,            -- '2026' or '2026-01' depending on the type
  scope_key     text,            -- '' for central, device short code for POS
  next_value    bigint NOT NULL,
  PRIMARY KEY (document_type, period_key, scope_key)
```

Allocation is a single atomic statement, safe under concurrency without an
explicit lock:

```sql
INSERT INTO core.document_counter AS c (document_type, period_key, scope_key, next_value)
VALUES (@type, @period, @scope, 2)
ON CONFLICT (document_type, period_key, scope_key)
DO UPDATE SET next_value = c.next_value + 1
RETURNING next_value - 1 AS allocated;
```

Formats

| Type | Format | Example | Allocated by |
|---|---|---|---|
| PurchaseOrder | `PO-{yyyy}-{000000}` | `PO-2026-000001` | server |
| GoodsReceipt | `GRN-{yyyy}-{000000}` | `GRN-2026-000001` | server |
| Transfer | `TRF-{yyyy}-{000000}` | `TRF-2026-000001` | server |
| Sale | `SAL-{yyyy}-{device}-{000000}` | `SAL-2026-D03-000812` | **device** |
| Return | `RET-{yyyy}-{device}-{000000}` | `RET-2026-D03-000014` | **device** |
| Adjustment | `ADJ-{yyyy}-{000000}` | `ADJ-2026-000001` | server |
| Count | `CNT-{yyyy}-{000000}` | `CNT-2026-000001` | server |
| Quarantine | `QRT-{yyyy}-{000000}` | `QRT-2026-000001` | server |
| Shift | `SHF-{yyyy}-{device}-{0000}` | `SHF-2026-D03-0042` | **device** |
| SupplierReturn | `SRT-{yyyy}-{000000}` | `SRT-2026-000001` | server |
| Receipt | `RCT-{yyyy}-{000000}` | `RCT-2026-000001` | server |

Device-scoped numbers embed the device short code so an offline device can issue
a globally unique, printable receipt number with no server round-trip. Server
never renumbers them; the `Guid` id remains the join key.

Numbers are allocated **at commit time**, not at draft creation, so cancelled
drafts do not burn numbers — except POS sales, where the number is allocated at
sale start because it is printed on the receipt.

---

## 11. Database roles and grants

| Role | Grants |
|---|---|
| `pos_migrator` | owner of all schemas; used only by the migration job and the grants step |
| `pos_app` | `SELECT, INSERT, UPDATE, DELETE` on every table in `core`, `catalog`, `inventory`, `purchasing`, `transfers`, `quarantine`; **only `SELECT, INSERT`** on `inventory.inventory_movement`, `audit.audit_log`, `core.login_attempt`, `core.receipt` (and `sync.processed_event` when Phase 13 adds it) |
| `pos_readonly` | `SELECT` on every table; used by the analytics connection |

`pos_app` cannot `TRUNCATE`, cannot `ALTER`, and has no rights on `pg_catalog`
functions that would let it drop the immutability triggers. This means even a
successful SQL-injection or a compromised application cannot rewrite history.

The grants live in `build/docker/initdb/02-grants.sql`. It runs as `pos_migrator`
after every migration (the compose `grants` service), grants per schema only for
schemas that exist, sets default privileges so tables added by later migrations
inherit the same rights, and is safe to re-run. `PostgresRoleGrantsTests` applies
the same file to the fully migrated schema and checks the privileges of every
table, so a table added later is covered without being named.

---

## 12. SQLite (client) differences

The device database holds a **scoped subset**:

| Kept in full | Cached (read-only mirror) | Not present |
|---|---|---|
| local `sale`, `sale_item`, `payment`, `cashier_shift` | `product`, `product_barcode`, `product_price`, `unit_of_measure`, `product_unit_conversion` | other locations' data |
| local `outbox_event`, `sync_state`, `sync_failure` | `location` (own + main), `supplier` (name only) | `audit_log`, `processed_event` |
| local `inventory_movement` (own location, retained 90 days) | `permission_snapshot`, `user_cache` | purchasing, other stores' transfers |
| local `inventory_balance` (own location) | `pre_approval_token` | reporting tables |

Differences:

- `numeric` is stored as **TEXT** and mapped to `decimal` by a value converter.
  SQLite's `REAL` is binary floating point and is never used for money or quantity.
  Ordering/aggregation on money is done in C#, or via `CAST(x AS NUMERIC)` where
  an approximate sort is acceptable for display only.
- `timestamptz` becomes TEXT in ISO-8601 round-trip format (`o`), always UTC.
- Concurrency uses an explicit `version INTEGER` column incremented by the
  `SaveChanges` interceptor instead of `xmin`.
- Partitioning, `EXCLUDE` constraints, `jsonb`, and `inet` are unavailable; the
  equivalents are `TEXT` + application validation. The overlapping-price rule is
  enforced by the sync writer, which only ever inserts server-vetted rows.
- The file is encrypted at rest with SQLCipher. The key is 256 random bits held
  in the platform secure store (DPAPI/Windows Credential Locker, Android
  Keystore) and applied as a raw key, so no passphrase derivation runs when a
  connection opens.
- Retention job prunes synced sales and movements older than 90 days, keeping
  aggregate shift totals for offline reporting.

---

## 13. Migrations

- EF Core migrations, one migration set per provider:
  `Pos.Infrastructure/Persistence/Migrations/Postgres` and `.../Sqlite`, selected
  by design-time factories `PostgresDesignTimeFactory` / `SqliteDesignTimeFactory`.
- Migrations are **applied by a dedicated job**, never by the API at startup in
  production (`ApplyMigrationsOnStartup` defaults to `false` outside Development).
- Raw-SQL migrations carry the triggers, partition scaffolding, the exclusion
  constraint, and the role grants; these are not expressible in the model builder
  and live in `Persistence/Sql/*.sql`, embedded as resources and executed from a
  migration's `Up`.
- Every migration is forward-only. Rollback is by restore-from-backup plus replay,
  because the ledger cannot be un-inserted.
