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

---

## 2. Authentication and devices

| Method | Route | Permission |
|---|---|---|
| POST | `/api/v1/auth/login` | anonymous, throttled |
| POST | `/api/v1/auth/login/pin` | anonymous, device-bound, throttled |
| POST | `/api/v1/auth/refresh` | anonymous + valid refresh token |
| POST | `/api/v1/auth/logout` | authenticated |
| POST | `/api/v1/auth/change-password` | authenticated |
| POST | `/api/v1/auth/2fa/*` | authenticated |
| GET | `/api/v1/auth/me` | authenticated — profile, locations, effective permissions |
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
| GET | `/api/v1/products` (search, filter, page) | `product.view` |
| GET | `/api/v1/products/{id}` | `product.view` |
| GET | `/api/v1/products/by-barcode/{barcode}` | `product.view` |
| POST | `/api/v1/products` | `product.create` |
| PUT | `/api/v1/products/{id}` | `product.edit` |
| POST | `/api/v1/products/{id}/barcodes` | `product.barcode.manage` |
| DELETE | `/api/v1/products/{id}/barcodes/{barcode}` | `product.barcode.manage` (retires, never deletes history) |
| POST | `/api/v1/products/{id}/prices` | `product.price.manage` |
| PUT | `/api/v1/products/{id}/location-settings/{locationId}` | `product.edit` |
| POST | `/api/v1/products/{id}/deactivate` \| `/activate` | `product.disable` |
| GET/POST | `/api/v1/categories` | read `product.view` / write `category.manage` |
| GET/POST | `/api/v1/brands` | read `product.view` / write `brand.manage` |
| GET/POST | `/api/v1/units` | read `product.view` / write `uom.manage` |
| GET/POST | `/api/v1/suppliers` | read `supplier.view` / write `supplier.manage` |

The product edit, barcode, price, location-settings and deactivate rows are
contracts agreed but not yet implemented: Phase 3 delivers location create +
settings, the catalogue reads, and the create commands for products, categories,
brands and units + suppliers. The deferred rows arrive with the catalog curation
phase.

`GET /products/by-barcode/{barcode}` returns `404` with
`errorCode: catalog.barcode_unknown` — the POS and receiving clients treat that
specific code as the trigger for the quarantine workflow, never as "create it".
`GET /products/{id}` returns `404` with `errorCode: catalog.product_unknown`.

`GET /products?q=...` matches the query against the product name (partial, via
`LIKE`), an exact SKU (the SKU is a value-converted key, so partial string
functions cannot translate through it), or any attached barcode (partial). The
`includeInactive`, `offset` and `limit` query parameters (`limit` is clamped to
200) apply the same way on this route and on `GET /suppliers`.

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
| POST | `/api/v1/inventory/adjustments` | `inventory.adjust` |
| POST | `/api/v1/inventory/adjustments/{id}/submit` | `inventory.adjust` |
| POST | `/api/v1/inventory/adjustments/{id}/approve` \| `/reject` | `inventory.adjust.approve` + tier |
| POST | `/api/v1/inventory/adjustments/{id}/reverse` | `inventory.adjust.approve` |
| POST | `/api/v1/inventory/counts` | `inventory.count` |
| POST | `/api/v1/inventory/counts/{id}/lines` | `inventory.count` |
| POST | `/api/v1/inventory/counts/{id}/submit` | `inventory.count` |
| POST | `/api/v1/inventory/counts/{id}/approve` | `inventory.count.approve` + tier |
| POST | `/api/v1/inventory/rebuild-balances` | `inventory.rebuild_balances` |
| POST | `/api/v1/inventory/reconcile` | `inventory.rebuild_balances` |
| GET | `/api/v1/inventory/exceptions/negative-attempts` | `inventory.view.all` |

There is **no** endpoint that sets a quantity. The only inventory-affecting
routes are document-driven.

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

```
GET    /api/v1/quarantine/incidents                  quarantine.view
POST   /api/v1/quarantine/incidents                  quarantine.create   -> LEDGER (to Quarantine)
POST   /api/v1/quarantine/incidents/{id}/photos      quarantine.create
POST   /api/v1/quarantine/incidents/{id}/investigate quarantine.investigate
POST   /api/v1/quarantine/incidents/{id}/link-product   quarantine.release + product.barcode.manage
POST   /api/v1/quarantine/incidents/{id}/register-product quarantine.release + product.create
POST   /api/v1/quarantine/incidents/{id}/release     quarantine.release  -> LEDGER (to Available)
POST   /api/v1/quarantine/incidents/{id}/reject      quarantine.reject   -> LEDGER (to supplier)
POST   /api/v1/quarantine/incidents/{id}/write-off   quarantine.reject + inventory.adjust.approve
```

---

## 8. POS

```
POST   /api/v1/shifts/open                           shift.open
POST   /api/v1/shifts/{id}/close                     shift.close
GET    /api/v1/shifts/{id}/summary                   shift.open
POST   /api/v1/sales                                 sale.create   (Idempotency-Key required) -> LEDGER
GET    /api/v1/sales?locationId&from&to&cashierId    sale.create | report.view
GET    /api/v1/sales/{id}                            sale.create | report.view
POST   /api/v1/sales/{id}/void                       sale.void     -> LEDGER (reversal)
POST   /api/v1/sales/{id}/reprint                    sale.reprint
POST   /api/v1/returns                               sale.return   -> LEDGER (to ReturnPending)
POST   /api/v1/returns/{id}/disposition              inventory.adjust -> LEDGER
POST   /api/v1/returns/{id}/refund                   sale.refund
GET    /api/v1/customers?query=                      customer.manage | sale.create
POST   /api/v1/cash-drawer/open                      cashdrawer.open_without_sale
```

`POST /sales` accepts the full sale as one payload and completes it atomically.
There is no "add line to server-side cart" chatter — the cart lives on the device.

---

## 9. Synchronization

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

## 10. Reporting and dashboard

```
GET /api/v1/reports/sales                     report.view
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

## 11. Notifications and admin

```
GET    /api/v1/notifications                 authenticated
POST   /api/v1/notifications/{id}/read       authenticated
POST   /api/v1/notifications/read-all        authenticated
GET    /api/v1/users                         user.manage
POST   /api/v1/users                         user.manage
PUT    /api/v1/users/{id}                    user.manage
POST   /api/v1/users/{id}/disable|enable     user.manage
PUT    /api/v1/users/{id}/roles              user.manage
PUT    /api/v1/users/{id}/locations          user.manage
PUT    /api/v1/users/{id}/overrides          user.manage
GET    /api/v1/roles                         role.manage
PUT    /api/v1/roles/{id}/permissions        role.manage
GET    /api/v1/permissions                   role.manage
GET    /api/v1/audit                         audit.view
GET    /api/v1/health/live  |  /health/ready anonymous (ready is IP-restricted)
```

---

## 12. SignalR

Hub `/hubs/ops`, bearer-authenticated, groups joined on connect:

- `loc:{locationId}` for each assigned location (or all, with `location.all`)
- `perm:{permissionCode}` for a small set of alerting permissions

Server → client methods: `NotificationRaised`, `StockLevelChanged`,
`TransferStatusChanged`, `QuarantineIncidentChanged`, `SyncHealthChanged`,
`DeviceDirective`.

Every message corresponds to a persisted `Notification` or change-feed entry, so
a client that was disconnected recovers the same information on reconnect.

---

## 13. OpenAPI

Generated at `/openapi/v1.json`, with the UI exposed only outside Production.
Each endpoint documents its required permission, so the generated spec doubles as
the authorization contract. `Pos.Api.IntegrationTests` asserts that the spec's
declared permission matches the attribute on every endpoint.
