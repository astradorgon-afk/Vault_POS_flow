# API Surface and Conventions

`Pos.Api` — ASP.NET Core Web API. Thin endpoints: build a command, dispatch,
map the `Result<T>`. No business logic in controllers.

---

## 1. Conventions

| Concern | Convention |
|---|---|
| Base path | `/api` |
| Versioning | `/api/v1/...`, header `X-Api-Version` accepted as an alternative |
| Auth | `Authorization: Bearer <jwt>` (API). Cookie auth is used only by `Pos.Web`. |
| Correlation | `X-Correlation-Id` echoed on every response; generated if absent |
| Device | `X-Device-Id` required on POS and sync endpoints; must match the token claim |
| Idempotency | `Idempotency-Key` on POST endpoints that create business documents |
| Errors | RFC 9457 `application/problem+json` with a stable `errorCode` |
| Paging | `?page=1&pageSize=50&sort=field:asc`, response `{ items, page, pageSize, total }` |
| Filtering | explicit named query parameters; no arbitrary filter expressions |
| Dates | ISO-8601 with offset; server treats everything as UTC |
| Money | JSON numbers from `decimal`, 4 dp; currency on the enclosing object |
| Concurrency | `If-Match` / `ETag` on mutable aggregates |
| Auth failures | `401` unauthenticated, `403` unauthorised, never `404`-as-`403` for records the user may know exist |

### 1.1 Error shape

```json
{
  "type": "https://vaultflow.local/errors/inventory/insufficient-stock",
  "title": "Insufficient stock",
  "status": 409,
  "detail": "Available 3, requested 5 at Store 1.",
  "errorCode": "inventory.insufficient_stock",
  "correlationId": "0192f2a1-...",
  "errors": { "lines[0].quantity": ["Exceeds available stock"] }
}
```

Status mapping: validation → `400`; unauthenticated → `401`; permission or scope
→ `403`; unknown id → `404`; state-machine or stock conflict → `409`; concurrency
→ `412`; rate limit → `429`; unexpected → `500` with no detail.

A body that is not valid JSON, or whose values cannot bind to the route (a
malformed id, an unknown enum name in a query string), is answered `400` with
`errorCode` `request.malformed` in every environment — never a `500` and never an
empty `400`.

A `403` refused by the route's own permission check (the caller holds the
permission nowhere) carries no body. A `403` refused inside the pipeline, such as
a location outside the caller's scope, carries the problem document.

---

## 2. Authentication and devices

| Method | Route | Permission |
|---|---|---|
| POST | `/api/v1/auth/login` | anonymous, throttled |
| POST | `/api/v1/auth/login/pin` | anonymous, device-bound, throttled |
| POST | `/api/v1/auth/refresh` | anonymous + valid refresh token |
| POST | `/api/v1/auth/logout` | authenticated |
| POST | `/api/v1/auth/change-password` | authenticated *(planned; administrators reset passwords today)* |
| POST | `/api/v1/auth/two-factor/setup` | anonymous, username + password, throttled — returns `sharedKey` and an `otpauth://` URI for an account that has not enrolled |
| POST | `/api/v1/auth/two-factor/enable` | anonymous, username + password + current code — turns two-factor on, returns eight one-time `recoveryCodes` |
| GET | `/api/v1/auth/me` | authenticated — profile, locations, effective permissions |

Sign-in (`/login`) takes an optional `twoFactorCode`: the authenticator code, or
one unused recovery code. When `Security:RequireTwoFactorForAdmins` is on (the
production default), an account holding `user.manage` or `role.manage` that has
not enrolled is refused with `403 auth.two_factor_enrolment_required` until it
completes the two enrolment calls above. Enrolment refuses an account that is
already enrolled (`409 identity.two_factor_already_enabled`) and a wrong code
(`400 identity.two_factor_code_invalid`).
| POST | `/api/v1/devices/enrol` | anonymous + enrolment code |
| GET | `/api/v1/devices` | `device.manage` |
| POST | `/api/v1/devices/{id}/suspend` \| `/revoke` \| `/reactivate` | `device.manage` |
| POST | `/api/v1/devices/enrolment-codes` | `device.manage` |

---

## 3. Master data

| Method | Route | Permission |
|---|---|---|
| GET | `/api/v1/locations` | `product.view` (list reads are deliberately relaxed so POS devices can resolve location scope offline) |
| POST | `/api/v1/locations` | `location.manage` |
| PUT | `/api/v1/locations/{id}/settings` | `settings.manage` |
| GET | `/api/v1/catalog/products` (search, filter, page) | `product.view` |
| GET | `/api/v1/catalog/products/{id}` | `product.view` |
| GET | `/api/v1/catalog/products/by-barcode/{barcode}` | `product.view` |
| POST | `/api/v1/catalog/products` | `product.create` |
| PUT | `/api/v1/catalog/products/{id}` | `product.edit` |
| POST | `/api/v1/catalog/products/{id}/deactivate` \| `/activate` | `product.disable` |
| GET | `/api/v1/catalog/products/{id}/barcodes` | `product.view` (retired codes included) |
| POST | `/api/v1/catalog/products/{id}/barcodes` | `product.barcode.manage` |
| POST | `/api/v1/catalog/products/{id}/barcodes/{barcode}/retire` | `product.barcode.manage` (retires, never deletes) |
| POST | `/api/v1/catalog/products/{id}/barcodes/{barcode}/primary` | `product.barcode.manage` |
| GET | `/api/v1/catalog/products/{id}/prices?locationId` | `product.view` |
| POST | `/api/v1/catalog/products/{id}/prices` | `product.price.manage` |
| GET | `/api/v1/catalog/products/{id}/location-settings` | `product.view` |
| PUT | `/api/v1/catalog/products/{id}/location-settings/{locationId}` | `product.edit` |
| GET/POST | `/api/v1/catalog/products/{id}/unit-conversions` | read `product.view` / write `product.edit` |
| DELETE | `/api/v1/catalog/products/{id}/unit-conversions/{conversionId}` | `product.edit` |
| GET | `/api/v1/catalog/products/{id}/suppliers` | `product.cost.view` (links carry the last cost) |
| PUT/DELETE | `/api/v1/catalog/products/{id}/suppliers/{supplierId}` | `product.edit` |
| GET/POST | `/api/v1/catalog/categories` | read `product.view` / write `category.manage` |
| GET/POST | `/api/v1/catalog/brands` | read `product.view` / write `brand.manage` |
| GET/POST | `/api/v1/catalog/units` | read `product.view` / write `uom.manage` |
| GET/POST | `/api/v1/catalog/suppliers` | read `supplier.view` / write `supplier.manage` |

`GET /products/by-barcode/{barcode}` returns `404` with
`errorCode: catalog.barcode_unknown` — the POS and receiving clients treat that
specific code as the trigger for the quarantine workflow, never as "create it".
A **retired** barcode answers exactly the same way. `GET /products/{id}` returns
`404` with `errorCode: catalog.product_unknown`, as does every
`/products/{id}/...` route for an unknown product.

`GET /products?q=...` matches the query against the product name (partial, via
`LIKE`), an exact SKU (the SKU is a value-converted key, so partial string
functions cannot translate through it), or any active barcode (partial). The
`includeInactive`, `offset` and `limit` query parameters (`limit` is clamped to
200) apply the same way on this route and on `GET /suppliers`. Product reads
return `defaultPurchaseCost: null` to callers without `product.cost.view`, and
list only active barcodes, primary first.

### 3.1 Catalog curation

Every change is audited in the same transaction (`product.updated`,
`product.cost.changed`, `product.activation.changed`, `product.barcode.changed`,
`product.price.changed`). Successful changes answer `204 No Content`; adding a
barcode, a price or a unit conversion answers `201 Created` (`{ "id": ... }` is the
product, the new price row and the new conversion respectively).

- **Edit** (`PUT /products/{id}`) replaces name, description, category, brand,
  primary supplier, tax code, VAT exemption, default cost and image. The base unit,
  batch tracking, expiry tracking and shelf life are fixed at creation.
- **Deactivate** needs `{ "reason" }` (at least 5 characters) and records the
  discontinuation date; activating clears it.
- **Barcodes** (ADR-0029): `{ "barcode", "unitOfMeasureId"?, "packQuantity" = 1,
  "isPrimary" = false }`. The first code attached becomes primary. Retiring needs
  `{ "reason" }`; a retired primary is replaced by the longest-attached active
  code. A retired value stays reserved.
- **Prices** (ADR-0029): `{ "amount", "reason", "locationId"?, "effectiveFromUtc"?
  (default now), "effectiveToUtc"? }`. A new price closes the one in effect; a
  temporary price resumes the old amount when it ends. `GET .../prices` marks the
  row in effect now with `isCurrent`.
- **Location settings**: `{ "isStocked", "minimumStock", "reorderPoint",
  "targetStock", "maximumStock", "preferredReplenishmentQuantity" }`, with
  `minimum <= reorder <= target <= maximum`. External counterparties are not
  stocking locations.
- **Supplier links**: `{ "supplierSku"?, "leadTimeDays", "minimumOrderQuantity"?,
  "isPreferred" }`. Marking one preferred un-marks the others; the last cost is
  recorded by goods receipts, never set here.

| Status | `errorCode` | When |
|---|---|---|
| 400 | `catalog.reason_required` | deactivation, retirement or price without a reason |
| 400 | `catalog.price_backdated` | price starting more than 5 minutes in the past |
| 400 | `product.price_effective_to` | price ending at or before its start |
| 400 | `product_location.thresholds_unordered` / `.quantity_negative` | inconsistent stocking thresholds |
| 404 | `catalog.barcode_not_attached` | retire/primary for a code the product does not have |
| 404 | `catalog.conversion_unknown` / `catalog.supplier_not_linked` | removing something not there |
| 409 | `catalog.price_overlap` | price that would replace a scheduled price or span several |
| 409 | `catalog.barcode_already_attached` | code held by this or another product |
| 409 | `catalog.barcode_retired` | code was retired and stays reserved |
| 409 | `catalog.product_already_active` / `_inactive` | activation state unchanged |
| 409 | `catalog.conversion_exists` | second conversion between the same units |
| 409 | `catalog.category_unknown` / `brand_unknown` / `supplier_unknown` / `uom_unknown` / `location_unknown` | reference does not exist (products carry no foreign keys to master data) |

---

## 4. Inventory

| Method | Route | Permission |
|---|---|---|
| GET | `/api/v1/inventory/balances?locationId&productId&state` | `inventory.view` (+ scope) |
| GET | `/api/v1/inventory/balances/summary?productId` | `inventory.view` |
| GET | `/api/v1/inventory/movements?...` | `inventory.movement.view` |
| GET | `/api/v1/inventory/movements/group/{movementGroupId}` | `inventory.movement.view` |
| GET | `/api/v1/inventory/products/{id}/history` | `inventory.movement.view` |
| GET | `/api/v1/inventory/documents/{type}/{id}/timeline` | `inventory.movement.view` |
| GET | `/api/v1/inventory/adjustments?locationId&status&offset&limit` | `inventory.view` (caller's locations) |
| GET | `/api/v1/inventory/adjustments/{id}` | `inventory.view` (+ scope) |
| POST | `/api/v1/inventory/adjustments` | `inventory.adjust` (+ scope) |
| POST | `/api/v1/inventory/adjustments/{id}/submit` | `inventory.adjust` (+ scope) |
| POST | `/api/v1/inventory/adjustments/{id}/approve` \| `/reject` | `inventory.adjust.approve` + tier (+ scope) |
| POST | `/api/v1/inventory/adjustments/{id}/reverse` | `inventory.adjust.approve` + tier |
| GET | `/api/v1/inventory/counts?locationId&status&offset&limit` | `inventory.view` (caller's locations) |
| GET | `/api/v1/inventory/counts/{id}` | `inventory.view` (+ scope) |
| POST | `/api/v1/inventory/counts` | `inventory.count` (+ scope) |
| POST | `/api/v1/inventory/counts/{id}/lines` | `inventory.count` (+ scope) |
| POST | `/api/v1/inventory/counts/{id}/submit` \| `/cancel` | `inventory.count` (+ scope) |
| POST | `/api/v1/inventory/counts/{id}/approve` \| `/reject` | `inventory.count.approve` + tier (+ scope) |
| GET | `/api/v1/inventory/counts/variances?locationId&productId&from&to&offset&limit` | `inventory.view` (caller's locations) |
| GET | `/api/v1/inventory/counts/repeat-variances?locationId&from&to&minOccurrences` | `inventory.view` (caller's locations) |
| POST | `/api/v1/inventory/rebuild-balances` | `inventory.rebuild_balances` |
| POST | `/api/v1/inventory/reconcile` | `inventory.rebuild_balances` |
| GET | `/api/v1/inventory/exceptions/negative-attempts?locationId&productId&from&to&offset&limit` | `inventory.view.all` |
| GET | `/api/v1/inventory/exceptions/negative-attempts/summary?locationId&from&to` | `inventory.view.all` |

There is **no** endpoint that sets a quantity. The only inventory-affecting
routes are document-driven.

### Stock adjustments (ADR-0031)

`POST /adjustments` takes `{ "locationId", "reason", "notes"?, "lines": [{ "productId",
"batchId"?, "state", "quantityDelta" }] }` (enums as numbers) and answers `201 { id }`.
Each line's unit cost is captured from the bucket; the detail shows it with the
line's `absoluteValue` and the `movementType` it will post as. The reason decides
the movement: `Damaged`/`Broken`/`Contaminated` → `Damage`, `Spoilage`, `Loss`,
`Theft`, `Expired` → `ExpiryQuarantine` from Available or `ExpiryWriteOff` from
Expired; all of these only remove stock. `Other` (notes ≥ 10 characters) may add
or remove. Approval posts in the same transaction, allocates the `ADJ-…` number and
refuses the author (`approval.self_approval_refused`), an approver out of tier
(`approval.tier_exceeded`) or out of scope (`approval.approver_out_of_scope`); a
post that would go negative answers `409 inventory.insufficient_stock` and leaves
the adjustment pending. Reject and reverse take `{ "reason" }` (≥ 5 characters);
reversal posts opposite `Reversal` movements.

| Status | `errorCode` | When |
|---|---|---|
| 400 | `adjustment.must_remove_stock` / `.state_not_allowed` / `.reason_not_allowed` / `.notes_required` | the reason cannot do what the line asks |
| 400 | `inventory_control.batch_required` / `.batch_not_allowed` / `.batch_mismatch` | batch does not fit the product |
| 403 | `inventory_control.outside_scope` | the document's location is outside the caller's scope |
| 409 | `adjustment.invalid_state` | wrong lifecycle state |

### Inventory counts (ADR-0031)

`POST /counts` takes `{ "locationId", "kind", "categoryIds"?, "productIds"?, "note"? }`:
`FullPhysical` takes no scope, `Category` needs categories, `ProductSpecific` needs
products, `Cycle` needs either (`400 count.scope_invalid`). The sheet lists the
available buckets in scope with their system quantity; products in scope without
stock appear with zero. `POST /counts/{id}/lines` takes `{ "lines": [{ "productId",
"batchId"?, "physicalQuantity" }] }`, may be called repeatedly, and re-reads each
bucket's system quantity at that moment. Submit needs every line counted
(`400 count.incomplete`) and flags repeat variances. Approval posts only the
variance under the `CNT-…` number and answers `409 count.stock_moved_since_counted`
with `staleLines` when stock moved after counting; reject returns the count to
counting, cancel ends it. Detail lines carry `systemQuantity`, `physicalQuantity`,
`variance`, `varianceValue` and `isRepeatVariance`.

The variance report lists varying lines of posted counts (default: last 90 days);
the repeat-variance report ranks product-location pairs that varied on at least
`minOccurrences` (≥ 2) posted counts, with `netVariance` and
`totalAbsoluteVarianceValue`.

### Negative-stock attempts

Every draw the ledger refuses is recorded, even though the refused command rolls
back (ADR-0030). The list returns attempts newest first, with the location code,
SKU, product name, bucket (`state`, `batchId`), `movementType`,
`requestedQuantity`, `availableQuantity`, `shortfall`, `policy`, the reference
document and number, the user, device and correlation id. The summary ranks
product and location pairs by `attempts`, then `totalShortfall`, with the first
and last attempt times. Both default to the last 30 days; `from` must precede `to`
(`400 inventory.report_range_invalid`).

### Reconciliation

Both routes respond with the same report:

```json
{
  "isHealthy": true,           // stored projection matches the ledger
  "wasRebuilt": false,         // true only on a rebuild that wrote rows
  "bucketCount": 34,           // buckets the ledger implies
  "rebuiltBucketCount": 0,
  "driftedBucketCount": 0,
  "discrepancies": []          // kinds: Missing, Quantity, AverageUnitCost,
                               // TotalValue, LastMovement, Unexpected
}
```

- `POST /api/v1/inventory/reconcile` is **read-only**: it replays the ledger and
  compares it with the stored projection. It never writes, so it is always allowed.
- `POST /api/v1/inventory/rebuild-balances` **drops and recreates** the
  projection from the ledger, so it is gated on configuration and returns
  `503 { "errorCode": "maintenance.disabled" }` when
  `Maintenance:AllowBalanceRebuild` is false. When no drift exists it writes
  nothing (`wasRebuilt: false`).
- Discrepancies are reported from the ledger's point of view: the **ledger is
  authoritative**, so a matching bucket that the ledger disagrees with is a
  projection bug, not a ledger one.

---

## 5. Purchasing

Implemented:

```
GET    /api/v1/purchasing/orders                            purchase.view
POST   /api/v1/purchasing/orders                            purchase.create
GET    /api/v1/purchasing/orders/{id}                       purchase.view
POST   /api/v1/purchasing/orders/{id}/submit                purchase.create
POST   /api/v1/purchasing/orders/{id}/approve               purchase.approve + tier
POST   /api/v1/purchasing/orders/{id}/reject                purchase.approve
POST   /api/v1/purchasing/orders/{id}/send                  purchase.approve   (marks Ordered)
POST   /api/v1/purchasing/orders/{id}/cancel                purchase.approve
POST   /api/v1/purchasing/orders/{id}/close                 purchase.approve   (reason required)
DELETE /api/v1/purchasing/orders/{id}                       purchase.create   (Draft only; withdraw)
POST   /api/v1/purchasing/orders/{id}/receipts              purchase.receive  -> LEDGER, atomically Posted
GET    /api/v1/purchasing/orders/{id}/receipts              purchase.view
GET    /api/v1/purchasing/orders/{id}/receipts/{receiptId}  purchase.view
POST   /api/v1/purchasing/receiving-discrepancies/{id}/resolve  purchase.discrepancy.resolve  (once only; 409 on repeat)
GET    /api/v1/purchasing/supplier-returns                  purchase.view
POST   /api/v1/purchasing/supplier-returns                  purchase.return
POST   /api/v1/purchasing/supplier-returns/{id}/submit      purchase.return
POST   /api/v1/purchasing/supplier-returns/{id}/approve     purchase.approve + tier
POST   /api/v1/purchasing/supplier-returns/{id}/reject      purchase.approve
POST   /api/v1/purchasing/supplier-returns/{id}/dispatch    purchase.return   -> LEDGER, allocates SRT number last
POST   /api/v1/purchasing/supplier-returns/{id}/confirm     purchase.return
GET    /api/v1/purchasing/supplier-returns/{id}             purchase.view
GET    /api/v1/purchasing/direct-delivery-authorizations    purchase.view    (newest first; ?activeOnly=true)
POST   /api/v1/purchasing/direct-delivery-authorizations    purchase.direct_to_store.authorize
POST   /api/v1/purchasing/direct-delivery-authorizations/{id}/revoke  purchase.direct_to_store.authorize
```

Receipts are created and posted in one atomic request (GRN number, receipt,
discrepancies, movements and the order's received totals commit together); there
is no mutable draft stage. A receipt line names its PO line, so the order detail
exposes each line's `id`.

Supplier returns source only `Damaged`/`Expired`/`Quarantine` stock (never
`Available`), carry a supplier authorization number on dispatch, and post the
balanced `Loc/<state> −q ⇄ EXT-SUPPLIER +q` group against the SRT number. A
failed dispatch never burns a sequence value. Drafts share the blank number, so
the unique index is filtered (`number <> ''`).

---

## 6. Transfers

Implemented (Phase 6 — main warehouse to store):

```
GET    /api/v1/transfers                             transfer.view (+ scope)
POST   /api/v1/transfers                             transfer.request
POST   /api/v1/transfers/{id}/submit                 transfer.request
POST   /api/v1/transfers/{id}/review                 transfer.approve
POST   /api/v1/transfers/{id}/approve                transfer.approve
POST   /api/v1/transfers/{id}/reject                 transfer.approve
POST   /api/v1/transfers/{id}/pick                   transfer.pick      (source scope)
POST   /api/v1/transfers/{id}/ready                  transfer.pick
POST   /api/v1/transfers/{id}/dispatch               transfer.dispatch  -> LEDGER
POST   /api/v1/transfers/{id}/cancel-dispatch        transfer.dispatch + transfer.approve
POST   /api/v1/transfers/{id}/receive                transfer.receive   -> LEDGER (dest scope)
POST   /api/v1/transfers/{id}/verify                 transfer.verify
POST   /api/v1/transfers/{id}/discrepancies/{d}/resolve  transfer.reconcile
GET    /api/v1/transfers/{id}/custody                transfer.view
```

Drafts share the blank number; the transfer number is allocated only on
dispatch (TRF-…, so a failed dispatch never burns a sequence) and the receipt
number only on receive (TRC-…). Picking is FEFO for transit-tracked products:
a pick that skips a still-available earlier lot is rejected
(`transfer.pick_skips_earlier_expiry`). Dispatch posts the balanced
`Available@source −q ⇄ InTransit@source +q` group; cancel-dispatch reverses it
against the same shipment number and requires a reason plus an approver. Receiving
posts the balanced `InTransit@source −q ⇄ Available@dest +q` group per allocation
(a shortfall lands in `TransitVariance`, damage in `Damaged`); resolving a
variance to Found returns it to `Available@dest`, and to Write-Off banks it at
the `EXT-WRITEOFF` location — two legs, so the ledger always sums to zero.
`partially received` transfers stay open until `verify` confirms every
discrepancy is resolved, which closes the order.

Phase 7 (store to store) — implemented:

```
POST   /api/v1/transfers/emergency                   transfer.emergency (dual auth body)
GET    /api/v1/transfers/pending-central-review      transfer.approve
POST   /api/v1/transfers/{id}/central-review         transfer.approve
POST   /api/v1/pre-approvals                         transfer.preapproval.issue
GET    /api/v1/replenishment/recommendations         transfer.request
```

Emergency transfers are the offline path: two co-signers on the originating
device move stock immediately, land in `PendingCentralReview`, and can be
ratified or rejected from HQ (a rejection reverses every unit with a recorded
reason). A per-store monthly cap bounds how many emergencies a store can open.
Pre-approval tokens are single-use, product- and route-scoped, value-capped,
validity-windowed, and revocable; a transfer created under a token submits
straight to `Approved`. Replenishment recommendations scope to the caller's
assigned locations and suggest the source with the most surplus — the Main
Warehouse first, then a sibling store.

---

## 7. Quarantine

Implemented (Phase 8). Routes live under `/api/v1/quarantine` (no `/incidents`
segment). Every command is **location-scoped** a second time inside the
application pipeline against the incident's own location, so an endpoint token
alone is never sufficient — a store manager raising at somebody else's store, or
at a system counterparty, is refused `403 auth.permission_denied` before any
business validation runs. Lists and details resolve scope from the database
authorization (locations the user is assigned to, or `location.all`), not from
the token, so the scope is exact when a user is assigned to several stores.

```
GET    /api/v1/quarantine                            quarantine.view          -> list, scoped
POST   /api/v1/quarantine                            quarantine.create         -> LEDGER (to Quarantine)
GET    /api/v1/quarantine/{id}                       quarantine.view          -> detail, 403 quarantine.outside_scope
GET    /api/v1/quarantine/{id}/photos/{photoId}      quarantine.view          -> photo bytes (image stored in the database)
POST   /api/v1/quarantine/{id}/photos                quarantine.create         -> attach photo (5 MB ceiling, base64 body)
POST   /api/v1/quarantine/{id}/investigate           quarantine.investigate   -> Open -> UnderReview
POST   /api/v1/quarantine/{id}/link-product          quarantine.release       -> identify existing product, LEDGER (to Quarantine)
POST   /api/v1/quarantine/{id}/register-product      quarantine.release       -> create product, then identify, LEDGER
POST   /api/v1/quarantine/{id}/release               quarantine.release       -> LEDGER (to Available), approver recorded
POST   /api/v1/quarantine/{id}/reject                quarantine.reject        -> LEDGER (to EXT-SUPPLIER), approver + reason
POST   /api/v1/quarantine/{id}/write-off             quarantine.reject + inventory.adjust.approve -> LEDGER (to EXT-WRITEOFF), whitelisted reasons
```

Notes:

- **Creating an incident** takes `{ locationId, lines: [{ barcode, quantity,
  unitCost?, claimedProductName? }], note? }`. Lines whose barcode already
  resolves to a product that does **not** track batches are identified at raise
  and their `QuarantineEntry` group posts immediately; batch-tracked and unknown
  barcodes wait for HQ identification (lot required for the former).
- **`link-product`** posts `{ lineNo, productId, batchId?, note? }`. The scanned
  barcode must already belong to the named product
  (`quarantine.barcode_not_owned`): identification never attaches a barcode, so
  the incident's raw code is never auto-linked by heuristic (see QUARANTINE.md
  invariants). A lot on a non-batch-tracked product is refused
  (`quarantine.batch_mismatch`); a batch-tracked product demands one
  (`quarantine.batch_required`).
- **`register-product`** posts `{ lineNo, sku, name, categoryId,
  baseUnitOfMeasureId, ..., batchId?, note? }`. The endpoint dispatches the
  catalogue `CreateProductCommand` (transitively requiring `product.create`,
  held by HQ roles) with the scanned barcode as the new product's primary
  barcode, then identifies the line against it. A pre-check that the line is
  still unidentified runs before the create is dispatched, so a conflict cannot
  normally create an orphan product; a failure between the two dispatches would
  still leave the product in the master (the barcode stays reserved).
- **`release` / `reject` / `write-off`** take `{ lineNo, quantity, note? }` plus
  `reasonCode` for write-off. Quantities are capped at what remains per line and
  an incident resolves only when every line is fully dispositioned
  (`quarantine.resolved` → 409 afterwards). `release` and `reject` still refuse
  an unidentified line (`quarantine.line_unknown`) and `reject` demands an
  approver and reason on the ledger legs. Write-off reasons are whitelisted to
  shrinkage codes (`quarantine.write_off_reason_not_allowed` otherwise) and the
  ledger requires `inventory.adjust.approve` as a second gate.
- **Disposition only from Quarantine**: entry posts `EXT-SUPPLIER/External −q ⇄
  Loc/Quarantine +q` (`QuarantineEntry`); release posts
  `Loc/Quarantine −q ⇄ Loc/Available +q` (`QuarantineRelease`, approver
  recorded, no reason); reject posts `Loc/Quarantine −q ⇄ EXT-SUPPLIER/External
  +q` (`QuarantineReject`, approver + reason); write-off posts to
  `EXT-WRITEOFF` under `QuarantineReject`. The supplier bucket carries negative
  stock while goods are in quarantine and nets back to zero on reject.
- **Errors:** `auth.permission_denied` (403), `quarantine.outside_scope` (403),
  `quarantine.location_external`, `quarantine.empty`,
  `quarantine.line_quantity_invalid`, `quarantine.line_unknown`,
  `quarantine.barcode_not_owned`, `quarantine.batch_required`,
  `quarantine.batch_mismatch`, `quarantine.line_already_identified`,
  `quarantine.resolved`, `quarantine.write_off_reason_not_allowed`,
  `quarantine.photo_data_invalid`, `quarantine.photo_too_large`,
  `quarantine.photo_unknown` (all 400/404/409 as appropriate).
- Incident numbers are `QRT-{yyyy}-{000000}`, allocated at creation (server-side
  counter).

---

## 8. Receipts

Implemented (ADR-0026, interim). A payment receipt is a standalone, RCT-numbered
record that a cash event happened at a branch — a walk-in sale, a branch expense
or an owner withdrawal. It posts nothing to the inventory ledger and tracks no
money balance; there is no subscription, invoice or usage metering behind it.

```
GET    /api/v1/receipts                  receipt.view     -> list, newest first, scoped to the caller's locations
POST   /api/v1/receipts                  receipt.create   -> 201 { id }, Location header; RCT number allocated
GET    /api/v1/receipts/{id}             receipt.view     -> detail, 403 receipt.outside_scope
GET    /api/v1/receipts/{id}/print       receipt.view     -> text/plain rendering for print or email
```

Notes:

- **Issuing** takes `{ locationId, kind, amount, counterparty?, note?,
  referenceNumber? }`. `kind` is the numeric `ReceiptKind` (`1` WalkInSale, `2`
  BranchExpense, `3` OwnerWithdrawal); the detail returns it by name. `amount`
  must be greater than zero and is stored `numeric(19,4)`, rendered to 2 dp.
  `counterparty` (≤ 128) and `note` (≤ 512) are trimmed. `referenceNumber`, when
  given, must parse as a business document number (`PO-2026-000017`,
  `SAL-2026-D03-000812`) and is stored upper-cased; it is a printed reference,
  not a foreign key.
- **Scope:** the command is location-scoped in the pipeline, so issuing at a
  store the caller is not assigned to — or, for a store-scoped user, at a system
  counterparty — is refused `403` before any business rule runs. A business-wide
  user who names an external counterparty gets `receipt.location_external`.
  Detail and print re-check `receipt.view` against the receipt's own location.
- **Who holds it:** Owner, Administrator, Main Inventory Manager and Store Manager
  (scoped) issue and view; Auditor views business-wide; Cashier and Inventory
  Staff hold neither.
- **Listing** takes optional `locationId`, `kind` (name or number), `from`
  (inclusive) and `to` (exclusive) ISO-8601 instants, `offset` and `limit`
  (1–200, default 100). A store-scoped caller only ever sees receipts at their
  assigned locations; filtering on another location returns an empty list rather
  than revealing it.
- **Rendering:** `ReceiptRenderer` produces the plain-text form (number, issue
  time in the branch's own time zone — `2026-09-14 10:52 (Asia/Manila)`, UTC only
  when the zone is unknown — location, type, amount, optional
  counterparty/reference/note, issuer). Emailing is out of scope; the rendering is
  the seam a thermal or PDF layout replaces.
- **Immutable:** a receipt is never edited or deleted — domain type, EF
  interceptor, database triggers and `pos_app` grants all refuse it. A wrong
  receipt is corrected by issuing another, not by changing the first.
- **Errors:** `receipt.location_external`, `receipt.location_unknown`,
  `receipt.kind_unknown`, `receipt.amount_invalid`,
  `receipt.counterparty_too_long`, `receipt.note_too_long`,
  `receipt.reference_number_invalid` (400); `receipt.outside_scope` (403);
  `receipt.unknown` (404).
- Receipt numbers are `RCT-{yyyy}-{000000}` from the shared server-side counter,
  allocated inside the issuing transaction, so a refused issue never burns one.

---

## 9. POS

```
POST   /api/v1/shifts/open                           shift.open
POST   /api/v1/shifts/{id}/close                     shift.close
GET    /api/v1/shifts/{id}/summary                   shift.open
POST   /api/v1/sales                                 sale.create   (Idempotency-Key required) -> LEDGER
GET    /api/v1/sales?locationId&from&to&cashierId    sale.view
GET    /api/v1/sales/{id}                            sale.view     -> detail, 403 sale.outside_scope
GET    /api/v1/sales/{id}/receipt                    sale.view     -> text/plain rendering, logs the first print
POST   /api/v1/sales/{id}/void                       sale.void     -> LEDGER (reversal)
POST   /api/v1/sales/{id}/reprint                    sale.reprint
GET    /api/v1/terminal/registers?locationId=        sale.create   -> the store's active browser registers
GET    /api/v1/terminal/session?locationId=          sale.create   (X-Device-Id) -> date, pricing settings, open shift
POST   /api/v1/terminal/{locationId}/next-number     sale.create   -> next server-minted SAL/RET/SHF for the register
POST   /api/v1/returns                               sale.return   -> LEDGER (to ReturnPending)
POST   /api/v1/returns/blind                         sale.return_blind -> LEDGER (to ReturnPending) + exception audit
POST   /api/v1/returns/{id}/disposition              inventory.adjust -> LEDGER
POST   /api/v1/returns/{id}/refund                   sale.refund   (branches on body `saleId`: referenced vs blind)
POST   /api/v1/customers                             customer.manage
GET    /api/v1/customers?search=&page=&pageSize=     customer.view
GET    /api/v1/customers/{id}                        customer.view
PUT    /api/v1/customers/{id}                        customer.manage
POST   /api/v1/customers/{id}/deactivate             customer.manage
POST   /api/v1/customers/{id}/reactivate             customer.manage
POST   /api/v1/cash-drawer/open                      cashdrawer.open_without_sale
GET    /api/v1/reports/daily-sales                   report.view   (re-checked against the location)
```

`POST /sales` accepts the full sale as one payload and completes it atomically.
There is no "add line to server-side cart" chatter — the cart lives on the device.

### Customers

Customer accounts are optional on a sale. Search matches display name, phone or
email case-insensitively, supports paging, and treats `%` and `_` as literal
characters. Create and update accept `displayName`, optional `phone`, `email`,
`tin` and `note`. Deactivation requires a reason; inactive customers remain
readable and may be reactivated, but cannot be attached to a new sale. Every
lifecycle mutation is audited without copying customer contact details into the
audit JSON fields. Unknown customer IDs return 404 `customer.unknown`; invalid
or inactive records return the corresponding `customer.*` validation error.

### Sales, receipt and first print

The sale is complete in one atomic request (SAL-numbered, device-scoped) against
an open shift on the same device, and the handler re-derives every economic fact
the device claims: the effective catalogue price at the completion instant
(`sale.item.price_missing` when none), the VAT classification, and the FEFO
allocation from the sellable shelf (`inventory.insufficient_stock` when the
shelf cannot cover the line; the expired-override exception path requires
`sale.expired_override`). Payments must cover the total exactly (`cash` records
`tendered` so the renderer prints the change). A sale needs the `EXT-CUSTOMER`
counterparty provisioned before it can post — the ledger posts store
Available → EXT-CUSTOMER. Cash, card and e-wallet may be mixed within one sale:
payment rows carry `method` (`1` Cash, `2` Card, `3` EWallet), `amount`,
optional `tendered` (cash only, may exceed `amount` to produce change) and an
optional `providerReference` (≤ 128 chars) for card/e-wallet; the web checkout
supports the full mix (C17), and a sum that differs from the total at 4 dp is
refused `409 sale.payment_mismatch`.

`GET /api/v1/sales/{id}` and the receipt route require `sale.view` and re-check
it against the **sale's own location**: a Store Manager of another store gets
403 `sale.outside_scope`, the Auditor reads business-wide. The receipt route
renders `SaleReceiptRenderer`'s plain-text form (branch wall-clock time, lines,
totals, payments with tendered/change) as `text/plain` and logs the first print
into the `ReceiptPrints` append-only log (reprints are C3's `sale.receipt.
reprinted`). Name-and-number contract mirrors the payments: `paymentMethod`
numeric (`1` Cash, `2` Card, `3` EWallet).

Errors: `sale.location_unknown`, `sale.location_external`, `sale.vat_rate_invalid`,
`sale.product_unknown`, `sale.product_inactive`, `sale.item.price_missing`,
`sale.discount_not_authorized`, `sale.price_override_not_authorized`,
`sale.expired_override_denied`, `sale.external_customer_missing`,
`sale.number_invalid`, `sale.number_device_mismatch`, `sale.device_unknown`,
`sale.shift_unknown`, `sale.shift_not_open`, `sale.shift_cashier_mismatch`,
`sale.shift_device_mismatch`, `sale.payment_mismatch`, `sale.customer_inactive`
(409); `sale.outside_scope` (403); `sale.unknown`, `sale.customer_unknown` (404).

### Web terminals

A browser register is an ordinary device of platform `Web`, activated when it
is created because the signed-in cashier's session is its credential. It has
no offline counter, so the server allocates its document numbers per device
(ADR-0032). A physical device is never numbered server-side: its counter
lives on the device and a server-minted number would collide with the numbers
it issues offline, so the number route refuses it `409 device.not_web`.

- `GET /api/v1/terminal/registers?locationId=` lists the store's active web
  registers as `{ id, shortCode, name }`. Pick one and send its `id` back as
  `X-Device-Id`.
- `GET /api/v1/terminal/session?locationId=` bootstraps the checkout,
  returning `{ deviceId, deviceShortCode, deviceName, businessDate, vatRate,
  cashRoundingIncrement, openShift }`. `businessDate` is the store's date in
  its own timezone; `openShift` is the shift open on that register
  (`{ shiftId, number, cashierId, cashierName, businessDate, openedAtUtc,
  openingFloat }`) or `null`. Errors: `device.required` (no `X-Device-Id`),
  `device.not_found`, `device.not_active`, `device.wrong_location`,
  `location.not_found`.
- `POST /api/v1/terminal/{locationId}/next-number` with body
  `{ documentType }` (`SAL`, `RET` or `SHF`) mints the next
  `PREFIX-{yyyy}-{shortCode}-{000000}` for the calling register via the shared
  atomic counter keyed by its short code, and returns
  `{ documentType, number }`. `SHF` additionally requires `shift.open` at the
  store (`authorization.denied`); an invalid type gets
  `terminal.document_type_invalid`; a physical or absent/misplaced register
   gets `device.not_web`/`device.not_found`/`device.wrong_location`.

### Web sale lifecycle

The browser web UI (`Pos.Web`) drives the sale-lifecycle flows against the
existing API routes. All permission checks happen on the server; the pages
gate their own controls client-side using the cached permission set.

**Sales search.** `GET /api/v1/sales?locationId&from&to` requires `sale.view`
and is scoped to the caller's assigned locations. Returns an array of
summaries (`{ id, number, status, businessDate, completedAtUtc, grossTotal,
netTotal }`) with `status` equal to `Completed` or `Voided`. The `from` and
`to` filters are business dates (ISO-8601 `yyyy-MM-dd`); `cashierId` further
narrows to one cashier. An invalid or unassigned `locationId` returns 404 or
403 `sale.outside_scope`.

**Sale detail and receipt.** `GET /api/v1/sales/{id}` (`sale.view`) returns
full detail: lines with product/batch/pricing, payments with method/tendered,
totals and the sale status. `GET /api/v1/sales/{id}/receipt` (`sale.view`)
returns the plain-text receipt. Both re-check `sale.view` against the sale's
own location. A reprint (`POST /api/v1/sales/{id}/reprint`, `sale.reprint`)
requires `reason` (≤ 200 chars) and appends to the `ReceiptPrints` log; the
route takes `locationId`, `deviceId`, `reprintedAtUtc` in the body and is
read-location-only (does not need an open shift).

**Void.** `POST /api/v1/sales/{id}/void` (`sale.void`) requires an open shift
on the same register (the register is identified by the `deviceId` header).
The web page sends `eventId`, `locationId`, `shiftId`, `deviceId`,
`businessDate`, `voidedAtUtc` and `reason`; the server dispatches the C1
`VoidSaleCommand` (ledger reversal, `sale.voided` audit).

**Return detail.** `GET /api/v1/returns/{id}` (readable with any of
`sale.view`, `sale.return`, `sale.refund`, `inventory.adjust`) returns the full
return: lines with product/batch info, returned quantities, refund totals
and per-line refund history. Returns 404 `return.unknown` or 403
`return.outside_scope`. The web page re-derives read permission from the same
union, so a cashier who can accept returns or run dispositions can always
open the page.

**Disposition.** `POST /api/v1/returns/{id}/disposition` (`inventory.adjust`)
takes per-line partial inspections with `quantity`, `kind` (1=Restock,
2=Quarantine, 3=Damaged, 4=SupplierReturn, 5=Waste), `reasonCode` and
`note`; does not require a register or shift. Immutable retry-safe events;
concurrency token prevents double consumption of the same units.

**Refund.** `POST /api/v1/returns/{id}/refund` (`sale.refund`) takes `saleId`
(null for blind returns), `method`, `amount`, `tendered`, `providerReference`
and `refundedAtUtc`; requires an open shift. The web page enforces cash-only
for blind returns; card/e-wallet refunds are refused at the form level and
at the server (`sale.refund.blind.cash_only`). Both referenced and blind
refund caps are enforced server-side.

### Returns and refunds

Returns and refunds are complete-in-one-request like the sale. `POST /api/v1/returns`
takes the referenced command as one payload: a `DocumentNumber.Parse`-validated
`number` (`RET-{yyyy}-{000000}`, device-scoped), `eventId` (replaying the same
event idempotently), `saleId`, `locationId`, `shiftId`, `deviceId`, optional
`customerId`, `businessDate`, `returnedAtUtc`, and `lines[{productId, quantity}]`
— FIFO-allocated against what each sale line still holds unreturned, refusing
`409 sale.return.quantity_exceeds_available` on overshoot and `404
sale.return.no_returnable_lines` for a product not on the sale. `POST
/api/v1/returns/blind` is the same without `saleId` and with a required `reason`
(≤ 200 chars); goods are priced at the catalogue at the returned instant and an
exception audit records the reason. Both return `201 Created` and post the
zero-sum `CustomerReturn` LEDGER group store `ReturnPending` ← EXT-CUSTOMER.

`POST /api/v1/returns/{id}/refund` refunds money against the return under
`sale.refund`. The body's `saleId` selects the path: present (and matching the
return's own sale) it is a **referenced** refund, capped per payment method by
what the original sale actually paid (`409 sale.refund.exceeds_paid_for_method`,
`409 sale.refund.method_not_original` when the sale was never paid that way)
and by the return's `RefundableTotal` (`409 sale.refund.exceeds_refundable`);
absent or mismatched it routes to the **blind** refund, cash-only and capped by
the return's own total with no per-method cap. A refund naming a different sale
than the return's is refused `409 sale.refund.sale_mismatch`. Cash refunds
carry `tendered` (refused if short, `sale.refund.tendered_insufficient`) and
respect the location's cash-rounding increment. Responds `200 { id }`.

Errors: `sale.return.quantity_exceeds_available` (409),
`sale.return.no_returnable_lines` (404), `sale.return.price_missing` (409),
`sale.refund.exceeds_paid_for_method`, `sale.refund.exceeds_refundable`,
`sale.refund.method_not_original`, `sale.refund.sale_mismatch` (409),
`sale.refund.blind.cash_only` (400, command validator).

### Return disposition

`POST /api/v1/returns/{id}/disposition` inspects one line of a referenced or
blind return. Requires `inventory.adjust` at the return's location. Body:

```json
{
  "eventId": "<new UUID, reuse unchanged for retries>",
  "locationId": "<return location UUID>",
  "lineNumber": 1,
  "quantity": 0.5,
  "kind": 1,
  "reasonCode": 6,
  "note": "Packaging sealed; inspected and approved for restock"
}
```

`quantity` must be positive with at most three decimal places; `note` is
required and limited to 512 characters. `reasonCode` is an existing
`AdjustmentReasonCode`. The server supplies the actor, timestamp and local
business date. Returns `200 { eventId }`.

| Kind | Destination |
|---|---|
| 1 — Restock | Available; rejects batches expired before today's local date |
| 2 — Quarantine | Quarantine plus an identified QRT incident |
| 3 — Damaged | Damaged |
| 4 — SupplierReturn | Damaged, awaiting the separate supplier-return workflow |
| 5 — Waste | EXT-WRITEOFF / External |

Each decision consumes only the named return line's remaining quantity.
The decision, zero-sum ledger group, optional incident and audit commit in one
transaction. Retrying an unchanged event returns the stored result; changing
its return, line, quantity, kind, reason or note is a conflict. Concurrent stale
line updates are refused with HTTP 412. Disposition does not issue a refund.

Errors under `sale.return.disposition.*`: `return_unknown` / `line_unknown`
(404), `location_mismatch`, `quantity_exceeded`, `expired`, `event_conflict`
(409), `contention` (412); malformed input is 400 and missing location authority is 403.

### Daily sales summary

> The sale lifecycle is completed by `POST /api/v1/sales/{id}/void` (`sale.void`):
> the body mirrors the completed sale (`eventId`, `locationId`, `shiftId`,
> `deviceId`, `businessDate`), so the handler re-checks the void against the
> sale's own location, shift, device and business date before reversing the
> original PosSale ledger group; `voidedAtUtc` and `reason` are recorded against
> the mandatory `sale.voided` audit. Idempotent by `eventId` — a retried void
> replays instead of double-posting.

`GET /api/v1/reports/daily-sales?locationId={id}&date={yyyy-MM-dd}` returns the
aggregated day for a location/business date: `salesSummary` (count, gross,
discount, net, VAT/exempt/zero-rated/taxable splits, `refundTotal`),
`paymentsByMethod` (method, amount, change given), and per-`shifts` rows (status,
opening float, sales count/net, `cashSalesTotal`, `cashRefundsTotal`). Only
`Completed` sales count; refunds are attributed through the shift that issued
them, so referenced and blind refunds both land on the day. `report.view` is
re-checked against the requested location: another store's manager gets
403 `report.outside_scope`, an unknown location gets 404
`report.location_unknown`.

---

## 10. Synchronization

```
POST   /api/v1/sync/push        authenticated device — batch of events, per-event results
GET    /api/v1/sync/pull        authenticated device — change feed after a cursor
GET    /api/v1/sync/baseline    authenticated device — full scoped snapshot (rebaseline)
GET    /api/v1/sync/status      authenticated device — checkpoints, pending review counts
GET    /api/v1/sync/failures    sync.manage — failed events across devices
POST   /api/v1/sync/failures/{id}/retry     sync.manage
POST   /api/v1/sync/failures/{id}/dismiss   sync.manage (reason required)
GET    /api/v1/sync/devices/health          sync.manage
```

Detailed contracts in [OFFLINE_SYNC.md](OFFLINE_SYNC.md).

---

## 11. Reporting and dashboard

```
GET /api/v1/reports/sales                     report.view
GET /api/v1/reports/daily-sales              report.view   (POS day summary, C7)
GET /api/v1/reports/sales/by-product          report.view
GET /api/v1/reports/sales/by-category         report.view
GET /api/v1/reports/sales/by-store            report.view
GET /api/v1/reports/sales/by-cashier          report.view
GET /api/v1/reports/sales/by-payment-method   report.view
GET /api/v1/reports/gross-profit              report.view.financial
GET /api/v1/reports/inventory/on-hand         report.view
GET /api/v1/reports/inventory/valuation       report.view.financial
GET /api/v1/reports/inventory/movement        report.view
GET /api/v1/reports/inventory/ageing          report.view
GET /api/v1/reports/inventory/dead-stock      report.view
GET /api/v1/reports/transfers                 report.view
GET /api/v1/reports/purchases                 report.view
GET /api/v1/reports/supplier-performance      report.view
GET /api/v1/reports/adjustments               report.view
GET /api/v1/reports/expiry                    report.view
GET /api/v1/reports/shrinkage                 report.view.financial
GET /api/v1/reports/count-variance            report.view
GET /api/v1/reports/unauthorized-inventory    report.view
GET /api/v1/reports/audit                     audit.view
GET /api/v1/reports/sync-problems             sync.manage
POST /api/v1/reports/{name}/export            report.export

GET /api/v1/dashboard/overview?range=today|yesterday|last7|last30|month|prev-month|quarter|year|custom
                              &from=&to=&locationId=
GET /api/v1/dashboard/exceptions
```

All report endpoints take the same `range` / `from` / `to` / `locationId`
parameters and are scope-filtered to the caller's assigned locations unless they
hold `location.all`.

---

## 12. Notifications and admin

```
GET    /api/v1/notifications                 authenticated
POST   /api/v1/notifications/{id}/read       authenticated
POST   /api/v1/notifications/read-all        authenticated
GET    /api/v1/audit                         audit.view
GET    /api/v1/health/live  |  /health/ready anonymous (ready is IP-restricted)
```

### User and role administration (implemented, ADR-0028)

```
GET    /api/v1/users?search=&includeInactive=&offset=&limit=    user.manage  -> list
POST   /api/v1/users                                            user.manage  -> 201 { id }
GET    /api/v1/users/{id}                                       user.manage  -> detail: roles, locations, overrides, effective permissions
PUT    /api/v1/users/{id}                                       user.manage  -> 204 name, e-mail, employee code, approval tier
POST   /api/v1/users/{id}/disable        { reason }             user.manage  -> 204, sessions ended
POST   /api/v1/users/{id}/enable                                user.manage  -> 204, lockout cleared
PUT    /api/v1/users/{id}/roles          { roles: [...] }       user.manage  -> 204 replaces roles
PUT    /api/v1/users/{id}/locations      { locations: [{locationId, isPrimary}] }  user.manage -> 204 replaces assignments
POST   /api/v1/users/{id}/overrides      { permissionCode, effect, reason, expiresAtUtc?, locationId? }  user.manage -> 201 { id }
POST   /api/v1/users/{id}/overrides/{overrideId}/remove  { reason }   user.manage  -> 204
PUT    /api/v1/users/{id}/password       { newPassword }        user.manage  -> 204, sessions ended
PUT    /api/v1/users/{id}/pin            { pin }                user.manage  -> 204 (employee code required)
POST   /api/v1/users/{id}/two-factor/reset  { reason }          user.manage  -> 204, key rotated, must enrol again
GET    /api/v1/roles                                            role.manage  -> roles with permissions and member counts
PUT    /api/v1/roles/{id}/permissions    { permissions: [...], reason }  role.manage -> 204 replaces the bundle
GET    /api/v1/permissions                                      role.manage  -> catalogue, isPrivileged marked
```

Every change is audited, bumps the authorization policy version (effective on
the caller's next request, without signing anyone out) and passes the safeguards
in ADR-0028:

| Error | Status | When |
|---|---|---|
| `identity.self_administration_forbidden` | 403 | changing your own roles, locations, overrides, tier, PIN, two-factor or status, or editing a role you hold |
| `identity.privilege_escalation_forbidden` | 403 | handing out a governance permission or approval tier you do not hold |
| `identity.target_outranks_caller` | 403 | changing an account or role holding governance authority you lack |
| `identity.last_administrator` | 409 | the change would leave no active account able to manage users and roles |
| `identity.user_unknown` / `identity.override_unknown` | 404 | |
| `identity.role_unknown`, `identity.permission_unknown`, `identity.location_unknown`, `identity.location_external`, `identity.primary_location_invalid`, `identity.password_rejected`, `identity.pin_invalid`, `identity.employee_code_required`, `identity.reason_required`, `identity.override_reason_required` | 400 | |
| `identity.username_taken`, `identity.employee_code_taken`, `identity.account_state_unchanged` | 409 | |

---

## 13. SignalR

Hub `/hubs/ops`, bearer-authenticated, groups joined on connect:

- `loc:{locationId}` for each assigned location (or all, with `location.all`)
- `perm:{permissionCode}` for a small set of alerting permissions

Server → client methods: `NotificationRaised`, `StockLevelChanged`,
`TransferStatusChanged`, `QuarantineIncidentChanged`, `SyncHealthChanged`,
`DeviceDirective`.

Every message corresponds to a persisted `Notification` or change-feed entry, so
a client that was disconnected recovers the same information on reconnect.

---

## 14. OpenAPI

Generated at `/openapi/v1.json`, with the UI exposed only outside Production.
Each endpoint documents its required permission, so the generated spec doubles as
the authorization contract. `Pos.Api.IntegrationTests` asserts that the spec's
declared permission matches the attribute on every endpoint.
