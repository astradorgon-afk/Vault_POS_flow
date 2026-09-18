# Progress Log

Running record of the work to close the known gaps and finish Phases 9–18.
Updated at every commit. [STATUS.md](STATUS.md) is the detailed state of the
system; [ROADMAP.md](ROADMAP.md) is the item-by-item plan. The standalone UI
mockup canvas is on hold; product UI now lands in tested implementation slices.

Legend: `[ ]` not started · `[~]` in progress · `[x]` done (with commit)

**Working agreement (2026-09-14):** gaps first, then Phases 9 → 18 in roadmap
order. One local commit per finished batch or phase, made only after the build
and the test suites pass. Nothing is pushed.

---

## Current position

**Now:** Phase 10 and the Phase 11 POS flow are committed. Phase 12 offline
storage is complete (C28–C33). **Phase 13 synchronization is complete at C56** —
outbox, push, every event applier, the retry queue, the change feed, the pull
route, the baseline a register starts from, the conflict rules and the full round
trip. Phase 14 notifications is complete at C57. **Phase 15 analytics
is under way**: C58 lands the sales analysis and margin, C59 the inventory reports, C60 ageing and dead stock, C61 transfers and distribution, C62 purchasing and supplier performance, C63 the inventory exception reports, C64 audit, quarantine and expiry, C65 CSV export. **Phase 16 is complete at C68**: the overview, the exception board and the document drill-down. **Phase 17 is complete at C69**: the CI coverage gate and the concurrency suites closed the two rows nothing answered; the other six were marked against the suites that already met them. C70 then repaired the two CI jobs that had been failing since the workflow was written. Next is Phase 18, deployment. Phase 11 was built as the "C" batch
series (customer-return and receipt work was built ahead of the
shift/sale/payment bulk). C1 (void), C2 (customer return + refund), C3 (receipt
reprint), C3b (blind return), C4 (blind-return refund), C5 (shift lifecycle
with cash reconciliation), C6 (sale endpoint surface + receipt render), C7
(daily sales summary report), C8 (sale pipeline tests + void route) and C9
(returns HTTP surface), C10 (return disposition), C11 (customer accounts), C12
(discount HTTP regressions), C13 (authenticated web shell), C14
(API-backed POS cart workspace), C15 (checkout orchestration with
server-numbered web terminals), C16 (web sale lifecycle: sales search,
receipt reprint/void and return/refund/disposition workflows in the browser),
C17 (card/e-wallet and split payment mixes in the web checkout), C18
(expired-batch sale blocking and the authorized override path — classified
`inventory.expired_only` refusals, per-line override denial checks, mandatory
recorded reason, web probe-then-confirm dialog) and C19 (scheduled-price
cancellation — `Product.CancelScheduledPrice`, the cancel endpoint under
`Permissions.Catalog.ManagePrices`, the `catalog.price_cancel_*` refusals and
the `product.price.cancelled` audit) are
complete; details are in the log below.
**Last commits:** C29b (raw-key device keying), C29 (protected change-feed application), C28 (device SQLite foundation), C27 (emergency-transfer alerts), C26 (receiving and transfer discrepancy alerts), C25 (low-stock alerts), C19 (scheduled-price cancellation — 7 new domain tests + 1 new integration test), C18 (expired-batch override contract — 5 new unit tests, 4 new integration tests), C17 (card/e-wallet + split payment checkout — 6 new integration tests, 0 backend changes), C16 (this commit — web sale lifecycle with 9 new backend tests), C15 (this commit — carries the C14 web shell and cart
workspace, which were never committed separately), `262724e` (C13 — authenticated web shell), `37a804f` (C12 — discount HTTP regressions), `d0f9723` (C11 — customer accounts), `7d9cddd` (C10 — return disposition), `66c419b` (C9 — returns/refunds endpoints), `232af28` (C8 — sale pipeline tests + void route), `7293608` (C7 — daily sales summary), `512269c` (C6 — sale endpoints + receipt), `b4ad864` (C5 — shift lifecycle), `c829307` (C3b — blind customer return),
`ac46de3` (C3 — receipt reprint with reason), `adf1a65` (C2 — customer returns
and refunds), `4a81731` (C1 — void completed sale), then Phase 10 as `70f4dda`.

---

## Plan

### Gap batches (before Phase 9)

- [x] **G1 — Correctness and operations**
  - [x] Quarantine line without a barcode returned 500 → validator, 400 `quarantine.barcode_required`
  - [x] Unreadable request bodies returned 500 (Development) or an empty 400 → 400 `request.malformed` problem document everywhere
  - [x] Printed receipts showed UTC → branch wall-clock time with the zone name
  - [x] Endpoint suite never ran the API on PostgreSQL → `PostgresHostSmokeTests` (proven to fail on the old numbering bug)
  - [x] `PosApiFactory` silently ignored overrides of default settings → overrides now win
  - [x] Grants script covered 4 schemas, one of which does not exist → every existing schema, PostgreSQL test over every table
  - [x] Receipts had no immutability guard → interceptor, triggers (migration `20260914090000_ReceiptIntegrityGuards`), append-only grants
  - [x] `docker compose up` could not start the API (no signing key, grants never applied) → Docker secret + key-per-file configuration, one-shot `grants` service
  - [x] Container images had never built (Dockerfiles omitted `.editorconfig`) → copied; `.dockerignore` keeps `.env`, `.secrets/`, `.git` out of the build context
  - [x] Fresh deployments ran two migrations out of order (15-digit ids sort differently by culture) → renamed to 14-digit ids; `MigrationOrderingTests`
  - [x] Caddy could not complete a TLS handshake (bare `:443` site) → `{$SITE_ADDRESS:localhost}`
  - [x] Whole compose stack verified end to end from a clean volume, over HTTPS, as `pos_app`
  - [x] `init-dev-secrets.ps1` required PowerShell 7 → runs on Windows PowerShell 5.1, writes the compose secret and `.env`
  - [x] Quarantine body without `lines` returned 500 → 400 `quarantine.empty`
  - [x] ADR-0027 (no retrying execution strategy); DEPLOYMENT, DATABASE, API, STATUS, ROADMAP updated (Phase 5 boxes ticked)
- [x] **G2 — Receipts completion:** `GET /api/v1/receipts` with location/kind/date filters and paging, scoped to the caller's locations (folded into the G1 commit)
- [x] **G3 — Phase 1–2 leftovers**
  - [x] User administration: create, update, disable/enable, roles, locations, overrides, password reset, PIN, two-factor reset
  - [x] Role administration: list roles and permissions, replace a role's permissions
  - [x] Safeguards (ADR-0028): no self-administration, hold-it-to-give-it for governance permissions and tiers, no overreach, always an administrator
  - [x] Seeder re-added removed default grants at every start → applied-once record + migration
  - [x] Two-factor enforced for user/role managers: password-authenticated enrolment, recovery codes, administrator reset
  - [x] Identity token writes were discarded under the no-tracking default (recovery codes reusable, reset did not rotate the key) → `TrackingScope`
  - [x] Serilog `SensitiveDataScrubber`
  - [x] Full test run (367 passed) and commit
- [x] **G4 — Phase 3 leftovers (catalog curation)**
  - [x] Product edit (tracking flags, shelf life and base unit stay fixed), deactivate/activate with reason
  - [x] Barcodes: attach, retire (stops scanning, stays reserved), set primary; migration `20260914100000_ProductBarcodeRetirement`
  - [x] Effective-dated prices: supersession, temporary prices that resume, no backdating; **pre-effective cancellation added later (C19, ADR-0029 consequence)** (ADR-0029)
  - [x] `ProductLocationSetting`, unit conversions, product–supplier links (one preferred)
  - [x] Reference checks (products have no foreign keys to master data)
  - [x] Primary-barcode swap failed on both engines (500) → demotions written first in `PosDbContext`
  - [x] Price overlap check treated the end as inclusive → half-open like the database
  - [x] Cashiers could read purchase cost → `product.cost.view` enforced on product reads
  - [x] Full test run (398 passed) and commit
- [x] **G5 — Phase 4 leftovers**
  - [x] Refused draws recorded after the command's rollback, append-only, with audit entries (ADR-0030); migration `20260914110000_NegativeStockAttempts`
  - [x] Exception report: list and per-product/location summary (`inventory.view.all`)
  - [x] Partitioning of `inventory_movement` and `audit_log` decided against for v1, with revisit thresholds (ADR-0030); DATABASE, DEPLOYMENT, SECURITY corrected
  - [x] Full test run (404 passed) and commit

### Phases

- [x] **Phase 9 — Inventory control** (ADR-0031)
  - [x] Stock adjustments: reasons mapped to write-off, expiry and correction movements; value-tiered approval by someone other than the author; reject; reversal
  - [x] Counts: full, cycle, category, product; sheet from the ledger; per-line re-read of system quantity; stale lines refused at approval; variance-only posting; reject to recount; cancel
  - [x] Variance and repeat-variance reports; repeat variance flagged on submission (90 days)
  - [x] Migration `20260914120000_InventoryControl`; full flow verified on PostgreSQL
  - [x] Removed six empty junk files from the repository root (three created by the local hook, three committed in Phase 1)
  - [x] Full test run (445 passed) and commit
- [x] **Phase 10 — Batch and expiration** (committed as `70f4dda`; the expiry run
  itself is done — FEFO allocation service extraction, POS sale-blocking and
  expiring-soon alerts are deferred to Phases 11/14, see log)
  - [x] Groundwork: `RequiredReferenceDocuments` set, `ExpiryQuarantine` justifiable by `StockAdjustment` or `ExpiryRun` (`G6`)
  - [x] Configurable expiry warning threshold (`LocationSettings.ExpiryWarningDays`, default 90)
  - [x] Expiry scan: expired, expiring-soon (threshold), FEFO-ordered sellable batches (`IExpiryService`)
  - [x] Expiry run: worker- or command-driven quarantine of past-expiry stock → `Expired`, EXP-numbered `ExpiryQuarantine` groups, system-actor audit
  - [x] FEFO transfer picking service (C22: transfer picks validate against the shared `FefoBatches` allocator)
  - [ ] Sale blocking for expired batches: POS consumers of the sellable-batch query + the authorized override path (Phase 11)
  - [x] Expiring-soon / expired alerts (C23: durable location-scoped notifications with stable deduplication keys)
- [~] **Phase 11 — POS** (returns side first as the "C" batch series)
  - [x] C1 — void a completed sale, same shift and business day, with ledger reversal and migration
  - [x] C2 — referenced customer return with refund limits and the `EXT-CUSTOMER → ReturnPending` ledger legs; refunds capped by the sale's payment mix; migration
  - [x] C3 — receipt reprint with mandatory reason and the append-only print log; migration
  - [x] C3b — blind customer return (no sale number): catalogue-priced, zero-sum `CustomerReturn` ledger group, exception audit with the reason, migration
  - [x] C4 — blind-return refund path: `RefundBlindSalesReturnCommand` (no `SaleId`), `IssueBlindRefund` (cash only, capped by `RefundableTotal`, no per-method cap), `sale.refund.issued` audit, handler tests, infra round-trip
  - [x] C5 — shift lifecycle with cash reconciliation: `ForceClose`, `Reconcile(varianceThreshold, reason)` with `IsForceClosed` flag; `Suspend`/`Resume`/`Reconcile` handlers, `ShiftCommandAccess` gate, `ShiftForceCloseWorker`, `GetForceCloseCandidatesAsync`, `AddShiftForceClose` migration; domain/handler/validator/infra tests
  - [x] C6 — sale endpoint surface and receipt render: `POST /api/v1/sales` (atomic full-sale payload through the `CompleteSaleCommand` pipeline), `GET /api/v1/sales/{id}` and `GET /api/v1/sales/{id}/receipt` behind the new `sale.view` permission re-checked against the sale's own location; `SaleReceiptRenderer` plain-text first print logged to `ReceiptPrints`; `SaleErrors.Unknown`/`OutsideScope`; endpoint tests through the real pipeline
  - [x] C7 — daily sales summary report: `GET /api/v1/reports/daily-sales` under the existing `report.view` permission re-checked against the report's location; completed sales + per-method payments + per-shift rows + refund amounts for a location/business date; `DailySalesReportRepository` aggregates via the `Refunds → CashierShift` join (referenced and blind refunds both count); `ReportErrors.LocationUnknown`/`OutsideScope`; endpoint tests (aggregation, refunds vs returns, 403 other-store, 404 unknown location)
  - [x] C8 — sale pipeline tests and the void route: `POST /api/v1/sales/{id}/void` dispatching the C1 `VoidSaleCommand` under `sale.void` (idempotent by `eventId`); integration tests through the real pipeline — insufficient stock 409 before payments, payment mismatch 409, VAT classification at the location rate, void restores the exact shelf quantity
  - [x] C9 — returns HTTP surface: `POST /api/v1/returns` (referenced, `sale.return`), `POST /api/v1/returns/blind` (blind, `sale.return_blind`), `POST /api/v1/returns/{id}/refund` (`sale.refund`, branches on body `saleId` → referenced vs blind); full-command payloads with `DocumentNumber.Parse`, RET-prefixed numbers, `eventId` replay safety, `201`/`200`; integration tests through the real pipeline — return+cash-refund E2E with the second-full-refund cap 409, over-quantity return 409, blind return with cash refund `200` and card refund refused `400`, refund naming a different sale `409 sale.refund.sale_mismatch`. Disposition route (`POST /api/v1/returns/{id}/disposition`) stays pending — no aggregate/command yet
  - [x] C10 — return disposition: partial line inspections into Available, Quarantine, Damaged, supplier-return staging or write-off; immutable retry-safe events, optimistic concurrency, quarantine incidents and zero-sum ledger posts; migration `20260915181631_SalesReturnDispositions`
  - [x] C11 — optional customer accounts: create/search/detail/update/deactivate/reactivate routes; `customer.view` and `customer.manage`; mutation audits without duplicated PII; sale completion accepts active known customers only; migration `20260916015216_CustomerAccounts`
  - [x] C12 — discount HTTP regressions: an authorized manual discount persists gross/discount/net/payment totals and its authorizer; a named authorizer without `sale.discount` gets 403 and leaves inventory unchanged
  - [x] C13 — authenticated Blazor shell: API-backed username/password/two-factor sign-in, circuit-scoped token session, protected routing and safe return URLs, sign-out, responsive operations navigation and workspace overview
  - [x] C14 — API-backed POS cart workspace: assigned-store selection, barcode lookup, product search, effective location/global pricing, quantity editing, removal and live totals
  - [x] C15 — checkout orchestration with server-numbered web terminals (committed with C14): register auto-select, session bootstrap, shift start, line discounts, cash payment and atomic submission; physical terminals refused `device.not_web`; 12 new `TerminalEndpointTests`
  - [x] C16 — web sale lifecycle: sales search (`GET /api/v1/sales` summaries), `GET /api/v1/returns/{id}` detail, `POST /api/v1/sales/{id}/reprint` reason+append-only; 9 new `SaleLifecycleEndpointTests`; web pages: sales search, sale detail (receipt/reprint/void/accept-return), return detail (disposition/refund/refund history); shared `TerminalBar`; `Pos.Web` builds clean
  - [x] C17 — card/e-wallet + split payment checkout: allocated-payment list, method tabs (cash/card/e-wallet), quick tender, provider reference; complete gated until allocated = net at 4 dp; 6 new `SalePaymentEndpointTests`, 0 backend changes
  - [x] C18 — expired-batch sale blocking + authorized override path: `inventory.expired_only` refusal classified against past-expiry coverage (shortfalls stay `inventory.insufficient_stock`); per-line `sale.expired_override` denial check (`sale.expired_override_denied` → 403); mandatory line reason (max 500) recorded in the `sale.expired.override` audit with the authorizing user stamped from context; web probe-then-confirm dialog with reason required; 5 new unit tests (3 validator + 2 handler) and 4 new `ExpiredOverrideEndpointTests`
  - [x] C20 — receipt formats: `Plain` remains default; `Thermal` renders every line at 42 columns for 80 mm printers; `Html` emits an escaped, self-contained 80 mm document. Both receipt endpoints select formats through `?format=Plain|Thermal|Html`; the web sale detail opens HTML in the browser print dialog for PDF output.
  - [x] C21 — browser price schedule: catalog search, current/history/scheduled price timeline, scoped scheduling form and cancellation reason flow, gated by `catalog.view` / `product.price.manage`.
  - [x] C22 — FEFO allocation extraction: transfer-pick validation calls the same `FefoBatches` allocator as sales, then compares submitted batch totals with its canonical slices.
  - [x] C23 — notification foundation and expiry alerts: durable notification rows, per-user read/acknowledgement receipts, unique deduplication keys, and expiring-soon/expired-run generation from the expiry worker; migration `20260916190504_AddNotifications`.
  - [x] C24 — live notification centre: authenticated, location-scoped list/read/read-all API; SignalR user/location groups with automatic reconnect; live unread badge and responsive operations signal ledger in the Blazor shell.
  - [x] C25 (low stock) — hourly `LowStockWorker` raises location-scoped alerts for active stocked products at or below a positive reorder point: Warning at the reorder point, Critical below minimum or out of stock; deduplicated per product, level and UTC day; no migration.
  - [x] C26 — 15-minute `DiscrepancyAlertWorker` alerts posted goods receipts with unresolved receiving discrepancies (Warning, receiving location) and short transfer arrivals (Critical, source and destination), each linked to its document; 7-day lookback; no migration.
  - [x] C27 — 5-minute `EmergencyTransferAlertWorker` announces committed emergency transfers as Critical alerts to both endpoint stores (and all-location HQ users), linked to the transfer; durable per-location deduplication and 30-day restart lookback; no migration.
  - [ ] Sale flow: discounts/VAT, payments, shift/device context and atomic completion wiring
- [x] **Phase 12 — Offline storage:** `Pos.Client` SQLite store, cache tables, device numbering, permission snapshots, status surface and the first executable device use cases
  - [x] C28 — Windows/Android MAUI Blazor Hybrid client; dedicated seven-table device schema; SQLCipher encryption with a 256-bit key held in platform `SecureStorage`; initial SQLite migration; cached product/barcode/price/location/user and permission-snapshot entities; money and UTC text converters; global/store snapshot uniqueness; architecture and encrypted-file regression tests.
  - [x] C29 — `ChangeFeedApplier` applies a validated page and its `sync_cursor` in one `BEGIN IMMEDIATE` transaction (replays recognised, gaps refused); cache, snapshot and cursor writes outside it refused by an EF interceptor and by per-connection-function SQLite triggers; migration `DeviceChangeFeedGuards`.
  - [x] C29b — device database keyed with its 256-bit key as a SQLCipher raw key (opens fell from 650–800 ms to about 2 ms); key read once per process, failed reads retried; initializer pragmas limited to the ones that outlive their connection, leaving `synchronous = FULL`.
  - [x] C29c — CI repair: the server job leaves `Pos.Client` out; new Android (Ubuntu) and Windows client jobs gated on it, with workloads pinned to set `10.0.301`; Release-only IDE0005 in `MauiProgram.cs` fixed. The Linux Android Release build is still unverified — the first CI run on the branch is its real test (STATUS.md §4).
  - [x] C33 — the first executable device use case: `local_cashier_shift` and `local_audit` (migration `DeviceLocalShiftAndAudit`); `DeviceUnitOfWork`, `DeviceSession`/`DeviceCurrentUser`, `DeviceAuditWriter`, `DeviceNegativeStockAttemptRecorder` and `DeviceShiftRepository`; cached locations now carry their settings through the feed; shift open, suspend and resume flip to `Registered` while close stays `Pending` (it reconciles against local sales a device does not carry). 8 end-to-end tests through a real container.
  - [x] C32 — offline status surface: `DeviceStatusProvider` reports storage, enrolment, connectivity, last-received store data and cached-authority expiry; the `DeviceStatusView` contract lives in `Pos.Shared` (which references nothing), so it cannot carry a path, key, address or feed position; `DeviceStatusBanner` in `Pos.SharedUI` renders one concern at a time, offline never counting as a warning. 27 new tests.
  - [x] C31 — device numbering and permission expiry: `document_counter` on the device (migration `DeviceDocumentCounter`), an atomic upsert joining the caller's transaction, refusing central types and any short code but its own; `DeviceSnapshotPermissionEvaluator` re-checks expiry at every evaluation, scopes grants, and refuses anything the catalogue does not mark offline-capable; the feed refuses a widening snapshot (`sync.feed_page_invalid`) and a policy-version rollback (`sync.snapshot_policy_rollback`). 24 new tests.
  - [x] C30 — client command boundary: `OfflineCommandCatalogue` declares the 24 offline use cases of OFFLINE_SYNC.md §1; `AddOfflineClientApplication` registers only those handlers and validators, no query handlers, with the server's behaviour pipeline unchanged; everything else answers `application.handler_unavailable` without touching a port. Entries stay `Pending` until the device carries `local_*` tables. 11 boundary tests.
- [x] **Phase 13 — Synchronization:** outbox, push/pull endpoints, idempotency behaviour, retries, conflict rules
  — every roadmap checkbox closed at C56. Three named things sit outside the
  phase's list and are written up in STATUS.md §"what is left": the scheduling
  loop that calls the uploader (client work, and the retry-queue entry always said
  so), feed retention and pruning (which is what makes a production `410`
  reachable), and a `UserChanged` emitter (the device caches users and the applier
  knows how to write them, but nothing on the server records the change).
  - [x] C34 — device outbox: `local_outbox_event` plus a single-row `device_sequence`, both allocated in the caller's transaction so a rolled-back event releases its number; `CanonicalJson` sorts object properties at every depth so declaration order cannot change a payload hash; `Environment.TickCount64` recorded as clock-tamper evidence; the device repositories enqueue business events, not row changes; the status banner now reports unsent work (POS.md §6). 18 new tests.
  - [x] C35 — the ledger on the device: `InventoryLedger` runs against `ILedgerStore`, implemented by both contexts, so there is one ledger rather than two; `local_inventory_movement` and `local_inventory_balance` reuse the server's own EF configurations; SQLite guards refuse rewriting history, deleting a balance, and writing a quantity outside the ledger's write window (migration `DeviceLedger`). 8 tests.
  - [x] C36 — cache what a sale reads: `cache_product.is_vat_exempt` and a new applier-owned `cache_batch` (with the C29 guards and a product/expiry index for FEFO), carried by a new `BatchChanged` feed kind; the validator refuses a batch expiring before it was received (migration `DeviceSaleCatalogue`). Found by reading `CompleteSaleCommandHandler`, not by running it.
  - [x] C37 — the sale's catalogue port: `GetSaleProductsAsync` returns a `SaleProduct` read model with the effective price already resolved, instead of the `Product` aggregate a device cannot rebuild; the blind return keeps whole aggregates on its own `GetReturnProductsAsync`, which it can because `sale.return_blind` is not offline-capable. Handler behaviour unchanged; server repository projects through `PriceAt`.
  - [x] C38 — device sale tables and adapters: `local_sale`/`local_sale_item`/`local_payment` from the server's own configurations (migration `DeviceLocalSale`); `DeviceSalesRepository`, `DeviceCustomerRepository`, `DeviceExpiryService`.
  - [x] C39 — the offline cash sale works: `DeviceExpiryService.GetSellableBatchesAsync` was an inner join from `cache_batch`, which returns nothing for untracked products and reads to the ledger as no stock; it now drives off balances and adds the no-batch item when the product is not batch-tracked, as the server does. `CompleteSaleCommand` is `Registered`. 5 end-to-end sale tests plus permanent coverage of the ledger's enlisted path; inverting the one condition reproduces the original failure in three of them.
  - [x] C40 — the offline shift close: `GetShiftCashTotalsAsync` reads cash payments from `local_sale`/`local_payment`, so a device reconciles its own drawer and records a variance rather than hiding one; refunds report zero because a device cannot refund yet, which must change the moment it can. `CloseShiftCommand` is `Registered`; C33's fail-closed test now uses `ReconcileShiftCommand`, which genuinely is central-only.
  - [x] C41 — offline void and reprint: a void returns stock by a reversing post and enqueues `SaleVoided`, keyed off the sale's own status rather than a flag on the shared port; reprints get `local_sale_receipt_print` (migration `DeviceReceiptPrint`) and enqueue `SaleReceiptReprinted`, because a second copy of a receipt can leave the shop. Both `Registered`.
  - [x] C42 — offline returns and refunds: `local_sales_return`/`local_sales_return_item`/`local_refund` (migration `DeviceLocalReturns`), enqueuing `SalesReturnCreated` and `RefundIssued`; `GetShiftCashTotalsAsync` now counts cash refunds, paying the debt C40 recorded; `GetRefundedAmountsByMethodAsync` stops a sale being refunded past what it was paid. Every POS entry in the capability table now executes on a device.
  - [x] C43 — push endpoint (`POST /api/v1/sync/push`): per-event transactions and one verdict per event; the idempotency record commits with the effect; a reused identifier carrying a different payload hash is refused as tampering (ADR-0007); a per-device checkpoint defers a gap and everything behind it, while a refused event still advances it; clock skew reported, not corrected. `sync.processed_event` and `sync.sync_checkpoint` (migration `SyncProcessedEvents`); `ShiftOpenedApplier` is the first applier. 8 tests.
  - [x] C44 — the shift appliers, and the event that was never sent: `DeviceShiftRepository.UpdateAsync` mapped a closed shift to no event at all, so a drawer counted offline closed locally and head office never learned of it — correct in C33, a bug the moment C40 registered `CloseShiftCommand`. `ShiftClosed` added, carrying the closing instant and the drawer figures. `ShiftSuspendedApplier`, `ShiftResumedApplier` and `ShiftClosedApplier` replay each transition through the aggregate, so a state the till would have refused is refused centrally too; a device claiming `isForceClosed` is refused, that being the server worker's authority. The close re-derives the variance from the sales the server accepted and records the device's own figure beside it when they disagree, rather than refusing money that has already moved. Routing the tests through the real processor found a second fault: two events touching one row in a batch collided, because an applier reads untracked and attaches what it read — each event now starts from a clean change tracker. 9 tests; commenting out the reset turns 3 of them red.
  - [x] C45 — the sale lands centrally: `SaleSyncPayload` now carries the lines and payments the cashier rang up, plus the device's `EventId`, because a header and a net total are not a business event the server can replay. `SaleCompletedApplier` calls `CompleteSaleCommandHandler` — the same handler the online endpoint runs — so the server re-derives price, VAT, FEFO and rounding from its own data; the SAL number, not the row id, is a sale's identity across the two sides. The handler is called directly rather than dispatched: the pipeline would authorize the device that uploaded the batch, where the rule is about the cashier who rang the sale up, and a cashier since unassigned has the sale flagged `RequiresReview`, never refused. Testing it over real HTTP found a C43 bug no unit test could: the sync checkpoint was read no-tracking, so it never advanced past the first event and every batch after a device's first would have deferred forever. Both sync fixtures now read no-tracking as the container does. `SyncOutcome` also goes on the wire by name, as OFFLINE_SYNC.md §3 always documented it.  7 tests.
  - [x] C46 — the quoted price version, closing C45's pinned gap: a sale line may name the `ProductPriceId` it was priced from, so a register that priced before a change and synced after it has its sale recorded at what the customer paid, against the row it was paid from. The device sends the identifier and never the amount — the server reads that back off its own row — and refuses a row that does not price this product at this location (`sale.item.quoted_price_not_applicable`) or that it does not hold (`sale.item.quoted_price_unknown`). Deliberately not a price override: an override means a person keyed a number in and another authorized it, and recording a stale price that way would put an entry nobody authorized on the override report. A quoted row that is no longer effective is accepted and written to the audit as `sale.price.variance`. `GetQuotedPricesAsync` is implemented on both sides, because the device runs the same handler. Reviewing the handler for it turned up a second engine fault: an applier that staged a row and then refused had that row committed alongside the refusal, so a sale the server turned away could still leave an audit entry saying it had happened. A refusal now rolls its transaction back and records the verdict in one of its own. `sync.event_type_unsupported` stays the deliberate exception — it records nothing and advances nothing, because the event is not wrong, the server is behind. 4 tests, 2 of them replacing the one that pinned the gap, plus a device-side assertion that the payload carries the row.
  - [x] C47 — void and reprint land centrally: both replay through their own handlers, and both resolve their sale by SAL number, the server having minted its own `SaleId`. `ISyncEventApplier` now receives the device's `EventId`, because an applier that posts to the ledger needs an idempotency key a retry reproduces and the protocol already has exactly one — the void's reversing post uses it, so a batch that times out after the ledger posted does not put the stock back twice. A follow-up whose sale the server does not hold is refused with `sync.sale_unknown` rather than dropped: under per-device ordering a missing sale means its upload was refused, and a void floating free of the sale it reverses would put stock back on a shelf against nothing. Both payloads widened to carry what their handlers need. 3 tests.
  - [x] C48 — the return and the refund, completing the upload: both replay through their own handlers, so the return carries only the products and quantities the cashier accepted and the server prices them from the sale's own line snapshots. A blind return is refused — `sale.return_blind` is not offline-capable, so a payload claiming one is a back door rather than a case. The refund finds its return by RET number and the handler re-checks against the server's rows that the sale is not refunded past what it was paid, which is the only place a second register refunding the same sale during one outage is caught. Every POS event a device can queue now lands centrally, and `SyncEventCoverageTests` compares `SyncEventType` against the appliers' own declared types and the container's own descriptors, so neither a new event with no applier nor an applier nobody wired up can ship quietly. A first draft of that guard read the registration out of the source file and passed with the line commented out. 6 tests.
  - [x] C49 — the retry queue, and the composition root it exposed: `SyncUploader` drains the outbox in sequence order, stopping at the first `Deferred`, and turns each verdict into a resting place — accepted and duplicate are done, deferred waits, rejected is never retried because the answer would not change, and no answer at all schedules a backoff. `SyncRetryPolicy` doubles from five seconds to a thirty-minute ceiling with ±20% jitter, which is what stops an estate coming back from one outage in lockstep; after eight consecutive failures an event is escalated rather than retried, and nothing is ever deleted. `HttpSyncTransport` reports every failure as one, including 401 and 403: a revoked device keeps its queue, because re-enrolling it is how that is fixed. The status surface now separates `UnsentEvents` from `EscalatedEvents` and warns without blocking. **Checking the DI found that `MauiProgram` never received the sale-side registrations of C38–C42** — the register would have thrown the first time a cashier rang something up, because every test composed its own container. One list now lives in `AddDeviceInfrastructure`, and `DeviceCompositionTests` resolves every `Registered` command's handler from it. 26 tests.
  - [~] C50 — the server's change feed: `sync.change_feed` is append-only and carries each change as the device will receive it, so serving a page is a read rather than a re-derivation from rows that have since changed again. `ChangeFeedRecorder` is a `SaveChanges` interceptor — a feed that depends on somebody remembering is one that silently stops carrying what nobody remembered — writing in the caller's transaction, so a rolled-back change takes its feed row with it. Its list of what a register caches is deliberate rather than reflective. The sequence comes from a single counter row inside that transaction, not a database identity: identities commit out of order, and a cursor that read past a lower number would never see it again. Writing the tests found two things — a cancelled price produced nothing, so a register would go on selling at a price the server no longer holds; and every strongly-typed id was serializing as `{"value":"..."}`, which a device would need a matching wrapper to read. 11 tests, migration `SyncChangeFeed`.
  - [x] C51 — the pull endpoint: `GET /api/v1/sync/pull` serves a device the changes that are everybody's and its own store's, scoped from its registration rather than from the query string, because making the scope a parameter would turn it into a way of asking for somebody else's catalogue. `nextCursor` is the server's answer rather than something a device infers: a full page stops at its last change, an unfilled one runs to the end of the feed, and without that a device would rescan the changes skipped for being another store's business on every pull for ever. `410 Gone { action: rebaseline }` covers both cursors the feed cannot honour — one ahead of the feed (a server restored from backup) and one whose missed changes have been pruned. 8 tests.
  - [ ] Retry queue with exponential backoff
  - [x] C52 — conflict rules: a replayed sale now posts to the ledger marked for review, which is what `AllowOfflineWithReview` was written to wait for and which nothing had ever set; a `NegativeStockAttempt` is recorded whether the draw was permitted or refused, because a permitted oversell is the one somebody most needs to find — it is the shelf that is now wrong — and recording only refusals meant the report showed every draw that did not happen and none that did. A sale of a product withdrawn while the till was dark lands and is flagged `sync.sold_a_withdrawn_product`, and the product stays withdrawn. Accepting an oversell end to end is pinned as not done: FEFO refuses before the policy is consulted, and deciding which batch carries a shortfall is a question about the allocator, not about sync. Two test helpers were mutating detached entities and passing for the wrong reason — the no-tracking default again. 3 tests.
  - [x] C53 — the failure queue and manual retry: `GET /api/v1/sync/failures` lists what head office turned away or set aside, read from `sync.processed_event` rather than a second table devices report into — everything on it is the server's own decision, so a reporting round-trip could only add a way for the two to disagree. `POST /api/v1/sync/failures/{eventId}/retry` re-applies nothing: a refused event earns the same answer unchanged, and it only becomes worth asking when a person changes what made the server refuse it. That ask travels as a `SyncRetryRequested` directive on the register's own feed, because a register that is offline cannot be told anything at all. The device reopens only an event that had stopped — one mid-backoff keeps its backoff, and an accepted one is never reopened, because asking a register to resend a sale head office already holds is how a day's takings get counted twice. The feed's sequence allocator moved out of the recorder so both writers share it. 4 tests.
  - [x] C54 — the round trip, and what it found: `ChangeFeedDownloader` is the join that was missing — the server had a route and the device had an applier, and nothing carried a page between them; a kind this build does not know is refused rather than skipped, because a register that quietly ignores half a feed and believes its catalogue current is the worse failure. PIN sign-in now puts the cashier's offline authority on the register's feed, which nothing had ever done, so a register could download a catalogue and still refuse to sell. Running a real device against the real server over HTTP then found three things no unit test could: `EXT-CUSTOMER` was scoped to itself, so no register ever received it and none could sell; the feed's currency was read from an organization row that is never written, so every location arrived with none and the page was refused; and the download outcome could not tell "nothing to do" from "the page was refused". The feed carries changes and not a starting state, so a new register still needs `/api/v1/sync/baseline`, which is not built — the test stands in for it. 2 tests.
  - [x] C55 — the baseline a register starts from, closing C54's pinned gap:
    `GET /api/v1/sync/baseline` projects the device's store, the external
    counterparties, every product with its barcodes and still-applicable prices,
    and every batch, as the very change records the feed carries — so the device
    writes it with the applier it already has. It is **not** a page of the feed:
    the sequences number the baseline's own rows and the feed's counter is left
    alone, because a baseline records nothing that happened and reserving numbers
    would push a register's cursor past rows the server had still to write. The
    cursor it resumes from is the feed's high-water mark read *before* the state
    is projected: a change that commits during the build then sits both inside the
    state and after the cursor and is applied twice, where reading the mark
    afterwards would let it fall between the two and be stepped over for ever.
    Writing the round trip without its stand-in found what the ordering alone does
    not fix — the server issues a permission snapshot and keeps no copy, so the
    feed row is the only record, and a baseline that walked the cursor past it left
    the cashier signed in and unable to sell. The baseline now reads the live
    snapshots back out of the feed, newest per user, dropping the revoked and the
    expired. A `410` is also no longer just reported: the register fetches a
    baseline in the same run and recovers on its own. 9 tests (8 unit, and the
    round-trip case that now reaches its own recovery).
  - [x] C56 — the oversell, end to end, closing C52's pinned gap and the last
    Phase 13 checkbox: `FefoBatches.Allocate` takes a shortfall flag, and when it
    is set the quantity the shelf cannot cover is added to the slice FEFO finished
    on rather than refused. The batch it names is the latest-expiring, which is
    the stock most likely to be actually standing there — a shelf holding more
    than the system says is usually a receipt nobody recorded, and a receipt
    nobody recorded is recent; when no batch is known at all the shortfall names
    none, because putting a number against a lot that never held it is worse than
    saying it is unknown. The flag is set only for a replayed offline sale at a
    location whose policy is `AllowOfflineWithReview`: an online sale has a
    terminal in front of it and a cashier who can be told, so it is deliberately
    unchanged. Nothing is waved through — the ledger still applies the policy to
    the draw, and the bucket is left negative rather than held at zero, because a
    balance that lies is how a count that never happens starts. Two things had to
    be fixed for the flag to mean anything: the applier's oversell check read a
    table nothing had written, because calling the handler directly skips the
    pipeline behaviour that flushes the recorder — the check could only ever have
    been false — and flushing from inside the applier deadlocked, the record going
    through a second connection the open transaction's own locks blocked. The
    applier now reads the recorder in memory and the processor flushes once each
    event's transaction has ended. Finding that turned up a third: the sale replay
    posted to the ledger under the payload's event identifier while the processor
    recorded its verdict under the envelope's, so a batch whose two disagreed
    could post the same sale twice. One key now, the protocol's. 6 tests.
- [x] **Phase 14 — Notifications:** persistence, expiry alerts, SignalR, the
  notification centre and every alert generator
  - [x] C57 — the sync-failure alert generator, Phase 14's last item and the one
    that waited on Phase 13: `SyncFailureAlertWorker` sweeps `sync.processed_event`
    every five minutes and raises a durable notification to the store whose
    register produced the verdict. It reads the same rows the failure list reads,
    because an alert derived from anything but the decision itself could disagree
    with the list somebody opens after reading it. A `Rejected` or `Conflict`
    verdict is Critical — the register's queue has stopped and a till can be
    trading all day with nothing reaching head office — and a `RequiresReview` one
    is a Warning, because the records are already central and somebody has to look
    rather than run. The server's own words travel in the body: "refused" without
    the reason only sends somebody to the failure list to be told what they were
    already being told. Deduplication is by event identifier, which matters more
    here than for the other generators — a register retries a refused event for as
    long as it stands, and an alert per retry would bury the one that mattered.
    The API suite runs the real sweep against the real container, which is the
    only place the join, the outcome filter and the lookback are exercised, and it
    takes the worker out of the host's own hosted services so a generator nobody
    registered cannot pass. 7 tests; no migration.
- [x] **Phase 15 — Analytics and reports:** sales, margin, inventory, transfers, purchasing, shrinkage, ageing, audit, export
  — every roadmap row delivered at C65. Two are marked `[~]` on purpose and are
  written up in STATUS: **turnover** proper needs a history of balances the system
  does not keep (a schema decision, not a reporting one), and **XLSX** needs a
  third-party spreadsheet library (a dependency decision). A third thing is open
  for a person rather than for code: `report.export` is granted to `Auditor` alone
  and the role matrix in PERMISSIONS.md has no row for it, so an Owner who may read
  every report cannot export one.
  - [x] C58 — sales analysis with margin: one `GET /api/v1/reports/sales` cut by
    `groupBy=Product|Category|Location|Cashier`, rather than the four `by-*`
    routes the API plan named — the rows, the totals and the margin arithmetic are
    identical in all four cases, and four routes would have been four places for
    the same rounding to drift. Gross profit is not a second report either:
    margin belongs on the rows that earned it. `GET /api/v1/reports/sales/payments`
    breaks the period's takings down by method. Writing the tests caught the one
    that would have mattered most — revenue was reading the sale line's **taxable
    base**, which is zero on a VAT-exempt line by design, so every exempt product
    would have reported as earning nothing and carrying a -100% margin. Revenue is
    the line's net amount less the VAT collected on it, which is right for all
    three tax classes. Three more decisions are pinned by tests: returns are
    reported as a column and never netted out of the period that sold the goods,
    because netting would rewrite a report somebody already printed; a line given
    away inside a paying sale reports no margin rather than dividing by zero; and
    the totals are computed from the same aggregates as the rows, so they still
    cover the whole period when the rows are truncated. Scope is read from
    `DatabasePermissionEvaluator` rather than the token — `ICurrentUser` answers
    `false` to business-wide authority whatever the caller holds, which would have
    narrowed an owner to one store — and `locationId` may only narrow, never
    widen. 18 tests.
  - [x] C59 — inventory on hand, valuation and movement history:
    `GET /api/v1/reports/inventory/on-hand` carries **no money** and needs only
    `report.view`; `/inventory/valuation` is the same rows with cost and value on
    them, behind `report.view.financial`. Stockroom staff can see what is on the
    shelf without seeing what it cost, which is why they are two routes rather
    than one route whose columns appear and disappear — a response whose shape
    depends on who asked is one no client can be written against. `available` is
    its own column because it is the only number a till may sell from; `onHand`
    is everything physically standing there; `inFlight` is dispatched and not yet
    received. Those groupings are read from `InventoryStates` in the domain rather
    than re-listed in a query, so a new state cannot appear in one place and not
    the other. Unit cost is derived from the totals, never averaged from the batch
    buckets — an average of averages weights a batch holding one unit the same as
    one holding a thousand, and the test uses exactly that shape to pin it. The
    counterparty leg is excluded from both the shelf counts and the movement
    history: writing the movement test showed every sale coming back twice, and
    counting the external bucket as stock would report the whole history of
    everything ever sold as sitting on a shelf. Movement history is ordered by
    when the ledger wrote a leg, not when it happened, because an offline sale
    uploaded on Tuesday occurred on Monday and a history reordered by occurrence
    would never tie back to a balance. 14 tests; no migration.
  - [~] C60 — ageing, slow movers and dead stock:
    `GET /api/v1/reports/inventory/ageing` buckets stock by how long the business
    has held it, measured from the batch's received date — not from manufacture,
    which is the supplier's business, and not from the last movement, which would
    reset every time a unit sold and report a pallet standing since spring as new.
    Stock whose product tracks no batches goes in an explicit `Unknown` bucket
    rather than the youngest one: it has no received date anywhere in the system,
    and calling it new makes the report say the opposite of the truth about the
    stock most likely to be old. `GET /api/v1/reports/inventory/dead-stock` counts
    `PosSale` legs rather than departures, so a write-off clearing a dead line does
    not make it look alive; a product that has never sold reports a null last-sold
    and sorts first, because a filter written as "last sold before X" would drop
    exactly what the report exists to find. **Turnover is deliberately not built.**
    A real turnover ratio needs the average stock held across the period and the
    system keeps balances rather than a history of them, so the report gives
    `daysOfCover` — null when nothing sold, because there is no rate to divide by
    and reporting infinity as a large number is how a line nobody can shift ends up
    looking merely slow. 7 tests; no migration.
  - [x] C61 — transfer and distribution reports: `GET /api/v1/reports/transfers`
    lists what was raised in a window, longest in flight first, and
    `/transfers/distribution` rolls dispatched transfers up by the lane they
    travelled. **A transfer is in scope when either end is** — it is as much the
    receiving store's business as the sending one's, and filtering on the source
    alone, which is the obvious way to write it, would hide every incoming
    shipment from the people waiting for it. Requested, dispatched and received
    stay three columns rather than one: collapsing them is how a shipment that
    arrived two cartons short comes to look complete. `daysInFlight` is null once
    a transfer is received, because there is nothing in flight to count.
    Distribution counts on dispatch and over the dispatch date — counting on
    receipt would make a lane look idle while a fortnight of stock sat in a van,
    and counting over the creation date would file this month's shipment under the
    month it was requested. The tests drive real transfers through Submit, Review,
    Approve, Pick, Ready, Dispatch and Receive rather than writing the columns, so
    a fixture cannot pass against a lifecycle the domain would refuse. 7 tests; no
    migration.
  - [x] C62 — purchase history and supplier performance:
    `GET /api/v1/reports/purchases` lists what was raised in a window with what has
    arrived against it, and `/supplier-performance` scores each supplier on
    punctuality, fill rate and quality. **Every rate comes with the count it was
    computed from**: a supplier who delivered once, late, scores 0% on time and
    reads identically to one who failed forty times, so `ordersScoredForTime` sits
    beside `onTimeRate` — the denominator is the difference between a verdict and
    an anecdote. Punctuality is scored only where an order was both promised a date
    and received; an unpromised delivery counted as on time would reward a supplier
    for refusing to commit to one, so the rate is null rather than perfect.
    `fillRate` is null when nothing was ordered, for the same reason. Lateness is
    measured against the **first** goods receipt rather than the last, because a
    trickle of back-orders months later should not rewrite whether the delivery was
    on time. The purchases report does carry the order's value under `report.view`,
    following the API plan's own permission table: what the business agreed to pay
    a supplier is not margin and not a stock valuation, and the person who raises
    and receives orders cannot do the job without seeing it. The receipts for a
    window are read in one query rather than per order, which is the shape that
    makes a quarterly scorecard time out in production and nowhere else. 7 tests;
    no migration.
  - [~] C63 — adjustments, shrinkage and count variance:
    `GET /api/v1/reports/adjustments` summarises stock written off or corrected by
    why, under `report.view` and carrying quantity only; `/shrinkage` is the same
    losses valued, behind `report.view.financial`; `/count-variance` lists the
    physical count lines that did not match. **Which movement types count as a loss
    is declared once in code** rather than inferred from the sign of a leg: a
    transfer dispatch and a count correction both reduce a bucket, and only one of
    them is stock the business no longer has. **A count correction is an adjustment
    and not shrinkage** — it says the books were wrong, not that goods left the
    building, and folding it in would let a business shrink its shrinkage by
    counting more often. Shrinkage counts only the legs that took stock away, so
    stock put back by an approved adjustment shows as `quantityIn` and never as a
    loss. Values come off the ledger leg, which recorded what the stock was carried
    at when it left; re-valuing at today's cost would move a closed month's figure
    every time a supplier changed a price. **A count line nobody counted is not a
    variance of zero** — it reports null, because "we looked and it was right" and
    "nobody looked" are different facts and conflating them makes an unfinished
    count read as a clean one. Writing the fixture ran into three domain rules that
    are each correct: a write-off needs an approver and a reason, a count
    adjustment must cite an inventory count rather than a stock adjustment, and a
    write-off cannot be reversed by flipping the signs on its own movement type.
    Still to come in this row: a dedicated expiry report, and unauthorized-inventory
    and quarantine reporting. 7 tests; no migration.
  - [x] C64 — audit activity, unauthorized inventory and expiry:
    `GET /api/v1/reports/audit` lists recorded actions under `audit.view`,
    `/unauthorized-inventory` the quarantine incidents, and `/expiry` the stock
    that has expired or is about to. **The activity list carries no
    before-and-after payloads** — they can hold customer details and prices, and a
    list is read far more often than a single entry is examined; whoever needs the
    payload opens the entry. What is returned includes the role snapshot, which is
    the authority the actor held at the time rather than now. **An audit entry with
    no location is business-wide** — a role change, a permission grant — and
    reaches only a caller who is not scoped to particular stores: showing it to a
    store manager would leak head-office activity through a report about their own
    shop, and hiding it from an owner would lose the entries that matter most.
    `daysOpen` ages an open incident to now and keeps how long a closed one took,
    so one number answers both questions, and `unidentifiedLines` counts what the
    catalogue does not know — unidentifiable stock is the most worth investigating.
    The expiry report always includes what already expired however far back it
    went, because a batch that expired last month is more urgent than one expiring
    next week rather than less. The sync-problems report the API plan named is
    `GET /api/v1/sync/failures` from C53 and is deliberately not duplicated: a
    second surface over the same verdicts could only add a way for the two to
    disagree. 8 tests; no migration.
  - [~] C65 — CSV export: `GET /api/v1/reports/{report}/export` returns CSV, a GET
    rather than the POST the API plan named because it reads and returns and
    creates nothing. **`report.export` gets you the file format, not the report** —
    each name is checked against the permission its own route requires before a row
    is read, so exporting is never a way round `report.view.financial`, which it
    would be if the route's own permission were the only gate. The name-to-permission
    table lives in one place, so adding a report to the export list without deciding
    who may read it is a compile error rather than an open door. Cells beginning
    `=`, `+`, `-` or `@` are prefixed with an apostrophe: a spreadsheet runs those
    as formulas, and a product named `=cmd|…` — enterable by anyone who can name a
    product — would otherwise run when a manager opened the file. Values go out
    invariantly, and a header row is always written so an empty period downloads a
    file that says what the columns were. **XLSX is not built**: it needs a
    third-party spreadsheet library, which is a dependency decision rather than a
    reporting one. Writing the tests surfaced a gap in the spec — `report.export`
    is granted to `Auditor` alone in the role bundles and the role matrix in
    PERMISSIONS.md has no row for it at all, so the permission-gap case had to be
    staged with a real `UserPermissionOverride`; who should be able to export is an
    open question. 9 tests; no migration.
- [x] **Phase 16 — Owner dashboard:** KPIs, store comparison, inventory and exception panels, drill-downs — every row closed at C68
  - [x] C66 — the overview: `GET /api/v1/dashboard/overview` gives a period's
    headline numbers, the store comparison and what the stock looks like now.
    **The sales half is the sales report** — it composes `ISalesAnalysisRepository`
    rather than running its own arithmetic, because a dashboard that queried
    separately would eventually disagree with the report a manager opens to check
    it, and somebody looking at two numbers for one week has no way to tell which
    is wrong. **Named ranges resolve in a real timezone**, named in the response:
    one store's own when a store is named, the organization's otherwise. "Today"
    in Manila is not the UTC day, and answering in UTC would show a store at nine
    in the morning a fraction of the day it had already had. `Last7` is seven days
    inclusive; `Custom` with a missing date is refused rather than falling back to
    today. **The financial half is withheld, not zeroed** — without
    `report.view.financial` the cost, profit, margin and stock value come back
    null, because zero reads as "we made nothing", a statement about the business
    rather than about the reader. The stock panel is a snapshot of now with its own
    `asOfUtc` whatever period the sales cover, and out-of-stock is counted apart
    from low because one is a sale being refused right now and the other a sale
    that will be refused next week. Running the suite caught a latent flake I had
    left in C55: `SyncBaselineProcessor` read `DateTimeOffset.UtcNow` directly, so
    its snapshot-expiry test passed in the morning and failed in the afternoon. The
    processor now takes `ISystemClock` like everything else. 26 tests (19 on the
    range resolver alone, where every case is an off-by-one somebody would
    otherwise find in a comparison and quietly distrust); no migration.
  - [x] C67 — the exception board: `GET /api/v1/dashboard/exceptions` returns all
    nine panels — unknown products, transfer discrepancies, high-value
    adjustments, negative-stock attempts, expired stock still sellable, repeated
    count variances, failed sync, offline devices and emergency transfers.
    **Every panel is present, including the clean ones**, because a board that hid
    its empty rows would leave a reader unsure whether there was nothing wrong or
    nothing looked at. **Each panel reports its whole count and a handful of
    examples**: a panel that said "5" because it had only looked at five would be
    the worst kind of wrong, since it would read as good news.
    `ExpiredStillSellable` is the one panel that is always Critical — every other
    exception is money or paperwork, and this one can reach a customer. Some panels
    are a standing state and some count the period, so the payload carries both the
    window and the instant it was read. A high-value adjustment's count is
    operational and its value financial, so a caller without
    `report.view.financial` still sees how many crossed the threshold; the
    threshold is a configured amount rather than a top-N, because "the ten largest"
    always finds ten even on a quiet week and trains people to ignore the panel. A
    register enrolled and never heard from counts as offline: a null last-seen date
    would drop it from a query written the obvious way, and a till nobody has ever
    heard from is either broken or in somebody's drawer. A first draft of the scope
    filter built its predicate from an expression tree — it worked and was
    unreadable, which is the wrong trade for a security filter, so each call site
    writes its own. 4 tests; no migration.
  - [x] C68 — the document drill-down: `GET /api/v1/dashboard/timeline` answers
    "what happened to this document" by merging the audit log, the ledger and the
    sync verdicts into one list, **each entry saying which source it came from** —
    a person chasing a discrepancy needs to know whether they are looking at
    something somebody did, something the ledger posted or something head office
    decided about an upload, and a merged list without the label invites reading
    one as another. Ordered by when the system recorded each thing rather than
    when it happened, because an offline sale uploaded on Tuesday occurred on
    Monday and sorting by occurrence would put its ledger posting before the shift
    that contained it; both times are carried so a reader sees the gap. A posting
    shows **every** leg, since stock leaving one bucket always arrives somewhere
    and a chain showing one side would look like stock vanishing. **A reversal is
    followable in both directions** — backwards-only would leave somebody reading
    the original with no sign it had been undone, which is the reading that counts
    the same loss twice. A document nothing in the caller's stores touched is a
    404 rather than an empty timeline: "you may not see this" and "nothing
    happened" are different answers. 6 tests; no migration.
- [x] **Phase 17 — Testing:** every row met — six by suites built across earlier phases, two by C69
  - [x] C69 — the coverage gate and the concurrency suites. **Coverage was
    collected and never looked at**: CI has passed `--collect:"XPlat Code
    Coverage"` since the workflow was written and gated on nothing. The raw
    number was 26.9%, which says nothing about the tests — EF's migration
    designer files and model snapshots are machine-written, never run outside a
    migration, and are 100,000 of the 160,000 lines counted. Excluding them and
    the generated OpenAPI file gives the honest figure: **80.4% of lines, 62.0%
    of branches**. `scripts/check-coverage.ps1` merges the six suites' Cobertura
    reports — a line counts as covered when any suite covered it — and fails
    under a total and a per-assembly floor set a couple of points below today's
    measurement, a ratchet rather than an aspiration. Two details decide whether
    that merge is arithmetic or fiction: each report's file names are relative to
    its own `<source>` root, so the same file appears under two names and
    Pos.Domain reads 14,798 lines at 67.6% instead of 7,403 at 83.8%; and a
    class's lines appear twice, under `<methods>` and under the class.
    `ExcludeByAttribute` is deliberately unset — excluding `GeneratedCodeAttribute`
    drops the whole `Pos.Application` module rather than the generated members in
    it, reading 36.8% with handlers the API tests drive showing 0%. **The
    concurrency rows** are the oversell case and the transfer races: four tills
    selling four each off a shelf of ten land exactly two sales and refuse two
    with `inventory.insufficient_stock`, six tills within stock all succeed and
    the projection's version is bumped once per sale. Two dispatchers shipping one
    transfer produce one shipment, two receivers one receipt — and what arbitrates
    that is not the aggregate's own guard, which reads the copy its context
    loaded and lets both writers past, nor a version token, which the transfer row
    does not have. It is the custody chain's unique `(transfer, sequence)` index,
    so the loser collides at the database. That index is the whole guard and
    nothing else would have noticed if it were relaxed. **The empty
    `Pos.Sync.Tests` was removed**: a `.csproj`, two `InternalsVisibleTo` grants,
    a place in the solution and no tests, while the sync tests it named live in
    `Pos.Infrastructure.Tests/Sync`, `/Offline` and `Pos.Api.IntegrationTests`.
    Reading OFFLINE_SYNC.md §10 against what exists found two rows genuinely
    unwritten, now recorded there: a sequence-gap **timeout**, which has no
    mechanism at all, and a backlog of the size that matrix names. 4 tests; no
    migration.
- [x] **CI repair (C70, out of phase):** the workflow has run twice, both on
  `main`, and both times two of its five jobs failed — unnoticed because the runs
  are on `main` only and the work happens on branches.
  **`verify-migrations` never restored**: `dotnet ef` builds the project it is
  pointed at, and a build with no assets file fails with `NETSDK1004` before it
  reads a model, so the check meant to catch a schema drifting from its code has
  never once run. It restores first now and both contexts come back clean.
  **The secret scan cried wolf forty times**, every finding a reference rather
  than a credential — `password = PosApiFactory.TestPassword` in thirty test
  files, `Password = RawKey(key)`, `PASSWORD=$(New-RandomPassword)`, and the
  `-----BEGIN RSA PRIVATE KEY-----` header the generator script writes into a key
  it never stores. Three fixes, each aimed at a shape: a private key needs a
  **body** to match; a value that is plainly **code** is a reference, and that
  test applies only to the assignment rules because a JWT would otherwise read as
  a member access and `Bearer` exists for those; and a value that **says it is
  not a credential** is taken at its word. Verified both ways — the repository
  comes back clean, and a scratch repository carrying a PEM key with a body, an
  `AKIA` key, a populated connection string, a password in YAML, an API key and a
  JWT is caught on all six while the false-positive shapes beside them stay
  quiet. `Format-Table` had also been printing findings as blank lines on a host
  that reports no width, so the one run that did fail printed nothing to act on.
  **Not yet proven green**: the workflow does not run on branches, so the next
  push to `main` is the first run that can pass.
- [ ] **Phase 18 — Deployment:** production compose overlay, backups/restore, logging/metrics, client packaging, CI publishing

---

## Log

### 2026-09-14

- **Checkpoint commit `d9a97b5`.** Interim payment receipts (ADR-0026); removed
  `EnableRetryOnFailure` (the API crashed at start-up on PostgreSQL); aliased the
  document-counter upsert (every document number failed on PostgreSQL);
  development API explorer at `/scalar`.
- **G1 + G2 finished.** Everything in the two checklists above. Verification:
  full solution **341 passed, 0 failed, 0 skipped** (Domain 160, Application 16,
  Infrastructure 37, Security 33, Architecture 13, API 82); no pending model
  changes; the whole compose stack built from scratch and exercised over HTTPS as
  `pos_app` (sign-in, receipt issue and print, quarantine raise with ledger
  posting, catalog/transfer/purchasing reads, `UPDATE core.receipt` refused).
  Running the real deployment found three problems no test could see: the images
  had never built, migrations applied out of order under invariant globalization,
  and Caddy could not complete a TLS handshake.
- **G1 + G2 committed** as `53346fe`.
- **G3 finished.** User and role administration with the ADR-0028 safeguards,
  two-factor enforcement for user/role managers, log scrubbing. Its tests found two
  more real defects: the seeder re-added default grants an administrator removed,
  and Identity's token writes were silently discarded under the no-tracking default
  (recovery codes reusable, two-factor reset not rotating the key). Verification:
  full solution **367 passed, 0 failed, 0 skipped** (Domain 160, Application 16,
  Infrastructure 37, Security 52, Architecture 13, API 89); no pending model changes.
- **G3 committed** as `58a3085`.
- **G4 finished.** Catalog curation: product edit, activation, barcode lifecycle,
  effective-dated pricing (ADR-0029), location settings, unit conversions and
  supplier links, all audited. Its tests found three defects: a primary-barcode
  swap could never be saved (EF Core wrote the promotion before the demotion and
  both engines refused it — proven on PostgreSQL by disabling the fix: `23505`),
  the domain refused adjacent price periods the database accepts, and every
  product read returned purchase cost to cashiers. Verification: full solution
  **398 passed, 0 failed, 0 skipped** (Domain 182, Application 16, Infrastructure
  39, Security 52, Architecture 13, API 96) with Docker running; no pending model
  changes. Docker Desktop hit the stale-socket start-up crash again and was
  recovered by renaming `%LOCALAPPDATA%\Docker\run` aside.
- **G4 committed** as `f5585c8`.
- **G5 finished.** Refused stock draws are now recorded — the documented
  shrinkage signal had never been written, and could not be written inside the
  refused command because its rollback would erase it; a pipeline behaviour writes
  the attempts after the unit of work ends, through a separate context. Report
  endpoints added; PostgreSQL triggers and grants verified. Partitioning decided
  against for v1 (ADR-0030) and the docs that described partitions corrected.
  Verification: full solution **404 passed, 0 failed, 0 skipped** (Domain 182,
  Application 20, Infrastructure 40, Security 52, Architecture 13, API 97) with
  Docker running; no pending model changes.
- **G5 committed** as `7642a18`.
- **Phase 9 finished.** Stock adjustments and inventory counts with approval,
  posting, reversal, variance and repeat-variance reports (ADR-0031).
  Verification: full solution **445 passed, 0 failed, 0 skipped** (Domain
  215, Application 20, Infrastructure 40, Security 52,
  Architecture 13, API 105) with Docker running; no pending model changes.
  Work stops here until resumed.
- **G6 (groundwork for Phase 10 �?" batch and expiration).** Movement-type rules
  now accept a **set** of reference document types per movement, not a single one:
  record is `RequiredReferenceDocuments` (`IReadOnlySet<ReferenceDocumentType>?`),
  a `Set(params ReferenceDocumentType[])` helper overload was added, every rule is
  set-wrapped, and `ExpiryQuarantine` is justifiable by both `StockAdjustment` and
  `ExpiryRun`. Reports/exports remain single-typed for now. Also adds the
  `ExpiryRun` groundwork in `DocumentNumber.cs` (`DocumentType.ExpiryRun = 15` /
  `EXP`) and `InventoryMovementGroup.cs` / `InventoryMovementType.cs`
  (`ReferenceDocumentType.ExpiryRun = 13`), uncommitted. Verification: full
  solution **445 passed, 0 failed, 0 skipped** (Domain 215, Application 20,
  Infrastructure 40, Security 52, Architecture 13, API 105); build 0 warnings
  0 errors; no pending model changes.
- **Phase 10 batch (expiry run, in progress, uncommitted).** The expiry worker and
  command built on the G6 groundwork: `LocationSettings.ExpiryWarningDays`
  (default 90, location JSON now carries the threshold); `ExpiryErrors`,
  `ExpiryRunResult` (`ExpiredBatchItem`, `ExpiringBatchSummary`, `SellableBatchItem`),
  `ExpiryRunRecordId`, `AuditActions.Expiry`, and the `inventory.expiry.run`
  permission (catalogue + privileged set); `RunExpiryCommand` with a thin handler
  (authorized, location-scoped, delegates to the service, writes an audit entry on
  success); `IExpiryService` / `IExpiryRepository` ports; the infrastructure
  `ExpiryService` (validates location + timezone, allocates one EXP document number
  per run, posts one two-leg `ExpiryQuarantine` group per expired batch
  `Available → Expired` with `AdjustmentReasonCode.Expired`, a shared `ExpiryRunRecordId`
  as the reference-document id, value summed at `Money.StorageScale` with
  `Money.IntermediateRounding`), `ExpiryRepository` (reads location settings,
  external-write-off location, stocking locations, warning days), `ExpiryWorker`
  (`BackgroundService`, `Expiry:Enabled` / `IntervalHours` [1–24] default 6 /
  `RunOnStartup`, per-scope sweep over stocking locations with a system actor
  `UserId.Empty`, skips locations without a timezone and `NoExpiredBatches`,
  tolerates per-batch failure), and   `ExpiryOptions` bound + data-validated in DI.
  Verification: build 0 warnings 0 errors; full solution **445 passed, 0 failed**
  (Domain 215, Application 20, Infrastructure 40, Security 52, Architecture 13,
  API 105) with Docker running. Batch cleared the commit gate.
- **POS C1 committed** (`4a81731`). Void a completed sale: the domain validates
  same-shift and same-business-day (same shift + same business-day gate), clears
  the sale's `IsVoided` flag (now excluded from daily summaries), and posts a
  full reversal `SaleVoided` ledger group referencing the original sale. Handler:
  shift/device facts check, writes the two `sale.created` and `sale.voided`
  audits with a mandatory reason (≤ 200 chars), persists through `AddAsync`. Tests
  for the domain rules, the handler's happy path and error paths, and the
  repository round-trip.
- **POS C2 committed** (`adf1a65`). Referenced customer return with refunds:
  `SalesReturn.Create` takes a `ReturnItemSpec` for each returned sale line
  (quantity capped at `SaleItem.Quantity − SaleItem.ReturnedQuantity`), freezes
  a proportional snapshot of the sale line's net paid amount, and validates the
  return fits inside `ReturnWindowDays` (default 7). The handler posts the
  zero-sum `CustomerReturn` group (`EXT-CUSTOMER/External −q ⇄ Store/ReturnPending
  +q`), writes the `sale.created` and `sale.return.created` audits, and persists.
  A second command issues a refund against the return, capped per payment method
  at what the return accepted. Refunds are posted through `AddRefundAsync` with
  a `sale.refund.issued` audit. `RefundCommandErrors` carries the sale-match,
  already-fully-refunded, and refund-exceeds-allowed caps. Tests: domain rules,
  both handlers, repository round-trips.
- **POS C3 committed** (`ac46de3`). Receipt reprint is read-only: the device
  re-emits the receipt from its local copy, so nothing sale- or ledger-changing
  happens — what must not be silent is the act of reprinting. `SaleReceiptPrint`
  (append-only, required-reason ≤ 200 chars for reprints, first prints carry no
  reason) is persisted by `AddReceiptPrintAsync`, with a `sale.receipt.reprinted`
  audit. Repository round-trip tests were added. The suite green at Domain 324,
  App 169, Infra 79.
- **POS C3b committed** (`c829307`). Blind customer return (POS.md §4) accepts
  goods back with no original sale under the same `CustomerReturn` ledger effect,
  plus an exception record — `sale.return.blind.accepted`. Domain:
  `SalesReturn.CreateBlind` (null `SaleId`, `IsBlind = true`,
  `BlindReturnItemSpec` as a flat record with product id/name/price/cost but no
  `SaleItemId`); VAT mirrored from sales (vatable/exempt/zero-rated proportional
  splits using `SalesReturnItem.SplitTaxInclusive`); `SaleItemId` made nullable
  on `SalesReturnItem` so both referenced and blind lines share the same table.
  Migration `20260915051436_AddBlindReturns` makes `sale_id` and `sale_item_id`
  nullable and adds `is_blind` (not-null, default false); the guard ran against
  PostgreSQL. Handler: `CreateBlindSalesReturnCommand` behind `sale.return_blind`
  (POS.md §4) — shift/device facts, RET numbering, catalogue price at the
  returned instant (no sale-line price available), the same zero-sum ledger group
  and both the `sale.return.created` and `sale.return.blind.accepted` audits with
  the exception reason. Referenced paths tightened: the existing handler refuses a
  null-backed sale item; the refund path rejects blind returns through its
  existing sale-id check (blind refund is a later batch). Tests: 11 domain tests
  (VAT splits, caps, reasons, empty and non-positive lines); 14 handler tests
  (happy paths, persist at today's price, product/location/external-customer
  errors, device/shift checks, ledger failure); two infra round-trips. The suite
   green at Domain 335, App 183, Infra 81 (with Docker).
- **POS C4 committed.** Blind-return refund path (POS.md §4): a blind return
  accepted goods back with no original sale, so its refund cannot be capped by
  the original sale's payment mix. Domain: `SalesReturn.IssueBlindRefund`
  (`IsBlind` guard, cash-only via `sale.refund.blind.cash_only`, capped only by
  the return's `RefundableTotal`, no per-method cap, cash tendered and rounding
  rules unchanged). `SalesReturnErrors.RefundBlindOnly` and `RefundBlindCashOnly`
  added. Application: `RefundBlindSalesReturnCommand` — no `SaleId`, same
  `sale.refund` permission (`IAuthorizedMessage`, `ILocationScoped`,
  `IIdempotentCommand`); validator mirrors `RefundSalesReturnCommandValidator`
  minus the sale rule, adds a cash-only constraint; handler pre-checks
  idempotency by event, then return exists / `IsBlind` /
  location/device match / shift open on same device, loads the location rounding
  increment, calls `IssueBlindRefund`, writes `sale.refund.issued` audit, persists
  via `AddRefundAsync`. `RefundCommandErrors.ReturnNotBlind` added for the
  handler-level check. Tests: 10 domain tests (cash-only guard, tendered / increment
  rules, cap accumulation, referenced-return refused blind-only); 16 handler tests
  (happy paths, idempotency, non-blind/location/device/shift errors, aggregate
  cap errors, persistence failure); one infra round-trip for a blind cash refund.
Full suite: Domain 345, App 200, Infra 64 (+ 18 skipped PostgreSQL guards),
   Security 52, Architecture 13, API 110 (2 Docker-absent Postgres-guard failures,
   unrelated). No migration needed: the refund table is C2.
- **POS C5 committed — shift lifecycle with cash reconciliation** (POS.md §1).
  Domain: `CashierShift.ForceClose(closedAtUtc)` closes from `Open`/`Suspended`
  and sets `IsForceClosed` (so a worker-closed shift cannot silently absorb the
  next day's sales); `Reconcile(decimal varianceThreshold, string? reason)`
  accepts a variance only when it is within the location's threshold, and a
  force-closed shift (whose `CashVariance` is null — the drawer was never counted)
  always requires a manager reason (`ShiftErrors.ReconcileReasonRequired`).
  `ShiftSuspended`/`ShiftResumed`/`ShiftReconciled`/`ShiftForceClosed` audit
  actions added. Application: `SuspendShiftCommand`, `ResumeShiftCommand`,
  `ReconcileShiftCommand` and their validators; three handlers sharing
  `ShiftCommandAccess` (owner check or `sales.close_other_shift` permission,
  location-scoped, audit + `UpdateAsync`); the reconcile handler resolves the
  location's `CashVarianceThreshold` through `LocationFacts`. Infrastructure:
  `ShiftForceCloseWorker` (`ShiftForceCloseOptions`, `IntervalHours` default 1,
  `RunOnStartup` default false) closing over-age open/suspended shifts via
  `IShiftRepository.GetForceCloseCandidatesAsync`; `is_force_closed` column;
  migration `20260915125042_AddShiftForceClose`. Tests: 13 domain, 25 handler, 12
  validator, 7 infra round-trips/candidates. Full suite: Domain 358, App 237,
  Infra 71 (+ 18 skipped PostgreSQL guards), Security 52, Architecture 13,
  API 110 (2 Docker-absent Postgres-guard failures, unrelated).
- **POS C6 committed — sale endpoint surface and the receipt render** (POS.md §2-3).
  New read-only permission `sale.view` (catalogue `Def(..., offline: true,
  readOnly: true)`, granted to Store Manager, Cashier and Auditor; the
  Administrator retains no `sales.*` grants by design, Owner inherits all via the
  wildcard). Domain: `SaleErrors.Unknown` (404 `sale.unknown`) and
  `SaleErrors.OutsideScope` (403 `sale.outside_scope`). API: `POST /api/v1/sales`
  maps `CompleteSaleBody` onto `CompleteSaleCommand` — `CashierId` comes from the
  authenticated request, `DeviceId` stays on the payload; the command's
  `IAuthorizedMessage` + `ILocationScoped` let the pipeline cap `sale.create` at
  the caller's locations (no body-scope route source exists or is needed), and
  the handler re-derives price, VAT and FEFO as before. `GET /api/v1/sales/{id}`
  and `GET /api/v1/sales/{id}/receipt` take `sale.view` and re-check it against
  the sale's own location. `SaleReceiptRenderer.RenderPlainText` emits the
  branch-wall-clock receipt (line list, totals, payment methods with
  tendered/change, `Thank you.`) — the same wire shape as the payment-receipt
  renderer, so the seam is identical — and the receipt route logs the first print
  (`isReprint: false`, no reason) into `ReceiptPrints`. No migration: permission
  flows through the seeder catalogue, the print table is C3. Tests: 5 endpoint
  tests on the real pipeline (complete + read + render + exactly one first print;
  price-missing 409; insufficient shelf 409; another store's manager 403 on
  detail and receipt while the Auditor reads; unknown sale 404). Full suite:
  Domain 358, App 237, Infra 71 (+ 18 skipped PostgreSQL guards), Security 52,
  Architecture 13, API 115 (2 Docker-absent Postgres-guard failures, unrelated).
- **POS C7 committed — daily sales summary report** (POS.md §2-3).
  `GET /api/v1/reports/daily-sales?locationId={id}&date={yyyy-MM-dd}` under the
  existing `report.view` permission (`Administration.ViewReports`) — no new
  permission, no migration. The route uses `Scope = ScopeSource.None` with a
  manual `IPermissionEvaluator` re-check against the location, so an
  other-store manager gets 403 `report.outside_scope`, while the Auditor (grants
  include `Administration.AllLocations`) reaches an unknown location to get 404
  `report.location_unknown`. Domain: `DailySalesReport` + summary/refund/payment
  shift records and `ReportErrors` (404 `report.location_unknown`, 403
  `report.outside_scope`). Infrastructure: `DailySalesReportRepository` in one
  AsNoTracking pass — only `Completed` sales for the location/business date;
  refunds attributed through `Refunds.CashierShiftId → CashierShift` so both
  referenced and blind refunds count toward the day even when the return is
  blind; payments grouped by method through the `SaleId` shadow key with
  change; per-shift cash sales/refunds for cash reconciliation. The wire model
  maps strong IDs to plain GUIDs. Tests: 4 endpoint tests through the real
  pipeline — two-sale aggregation (cash + cash/card split, per-method amounts
  and change, shift row) and the refund-vs-report leg (a referenced return with
  a 45 cash refund seeded through the public `SalesReturn.Create`/`IssueRefund`
  surface shows up in `refundTotal` and the shift's `cashRefundsTotal`), 403
  for another store's manager but OK for the Auditor, 404 unknown location.
  Full suite: Domain 358, App 237, Infra 71 (+ 18 skipped PostgreSQL guards),
  Security 52, Architecture 13, API 119 (2 Docker-absent Postgres-guard
  failures, unrelated).
- **POS C8 committed — sale pipeline tests and the void route.**
  `POST /api/v1/sales/{id}/void` completes the sale lifecycle HTTP surface:
  `VoidSaleBody` (`eventId`, `locationId`, `shiftId`, `deviceId`, `businessDate`,
  `voidedAtUtc`, `reason`) dispatches the C1 `VoidSaleCommand` under `sale.void`
  (the command is `IAuthorizedMessage` + `ILocationScoped`, so the pipeline caps
  it at the caller's assigned locations) with `VoidedByUserId` taken from the
  authenticated request — no migration, no new permission. Idempotent by
  `eventId`, so a retried void replays instead of double-posting the reversal.
  Tests: 4 integration tests through the real pipeline — selling past the
  sellable shelf is refused 409 `inventory.insufficient_stock` before payments
  are considered; payments under-covering the net total are refused 409
  `sale.payment_mismatch`; a non-exempt default product's line records the
  location VAT rate (12%), `isVatExempt` false, `isZeroRated` false ($2.6 — no
  zero-rated product flag yet); and voiding a completed £90 sale of 2 units
  restores the shelf to its exact pre-sale quantity. The suite pinned two facts
  on the way to green: the sale-detail route exposes lines as `lines` (not
  `items`), and PIN sign-in requires the device GUID in `X-Device-Id` — the
  registered device code string is refused with 400 `auth.device_header_required`.
  Full suite: Domain 358, App 237, Infra 71 (+ 18 skipped PostgreSQL guards),
  Security 52, Architecture 13, API 123 (2 Docker-absent Postgres-guard
   failures, unrelated).
- **POS C9 committed — returns HTTP surface.**
  Wires the C2/C3b/C4 returns and refunds up as endpoints through the real
  pipeline. `POST /api/v1/returns` (`sale.return`) dispatches the referenced
  `CreateSalesReturnCommand`, `POST /api/v1/returns/blind` (`sale.return_blind`)
  the blind `CreateBlindSalesReturnCommand`, and `POST /api/v1/returns/{id}/refund`
  (`sale.refund`) branches on the body's optional `saleId` — present dispatches
  `RefundSalesReturnCommand`; absent dispatches `RefundBlindSalesReturnCommand`.
  All three take the full command as one payload including a
  `DocumentNumber.Parse`-validated `number` (`RET-{yyyy}-…`), an `eventId` for
  replay safety, identity fields resolved from the authenticated request, and
  respond `201 Created` (returns) or `200` (refunds). `RequirePermission` is
  `Scope = ScopeSource.None` on all three. No migration, no new permission.
  **Not in C9:** the `POST /api/v1/returns/{id}/disposition` route from the
  plan stays pending — `SalesReturn` has no disposition aggregate method or
  application command yet (only the `InventoryMovementType.ReturnDisposition`
  code and its `MovementTypeRules` entry exist).
  Tests: 4 integration tests through the real pipeline — a referenced return
  `201` then a full cash refund `200`, with a second full refund of the same
  value refused `409 sale.refund.exceeds_paid_for_method` (the per-method
  running total from the sale-wide query plus the return's in-memory refunds
  double-counts the same payment method); returning more than the sale sold
  refused `409 sale.return.quantity_exceeds_available`; a blind return `201`
  with its cash refund `200` and the card refund refused up-front `400
  sale.refund.blind.cash_only` (command validator); and a refund naming a
  *different* sale refused `409 sale.refund.sale_mismatch`. Full suite:
  Domain 358, App 237, Infra 71 (+ 18 skipped PostgreSQL guards),
  Security 52, Architecture 13, API 127 (2 Docker-absent Postgres-guard
  failures, unrelated).
- **Note for the workstation:** a local hook echoes prompts and commands through
  `cmd`, so any `>` in that text creates an empty stray file in the repository
  root (seen as `,-`, `,session_title`, `%{redirect_url}'`). They were removed each
  time; none were committed.

### 2026-09-16 — POS refund follow-up

- Fixed the C9 refund double count: the handler now excludes the active return
  from the repository's prior-refund totals, because `SalesReturn.IssueRefund`
  already includes that return's loaded refunds. Refunds from other returns
  still count toward the original payment-method cap. No migration required.
- Added an HTTP regression covering two returns against a 90 sale, each refunded
  in 30 + 15 installments, with every event replayed. It verifies exactly four
  persisted refunds totaling 90, the per-return cap after the first return, and
  the original-payment cap after the second. Corrected the existing over-refund
  test to expect the per-return error when the original payment still covers it.
- Extended repository coverage for excluding one return while preserving the
  others, and handler coverage for passing the active return's ID.
- Updated `STATUS.md` next steps to reflect the endpoints already delivered.
- Validation: 158 targeted tests passed (Domain 39, Application 69,
  Infrastructure 32, Architecture 13, API 5); `git diff --check` passed.
  The full suite and PostgreSQL/Docker tests were not run for this follow-up.

### 2026-09-16 — C10 return disposition

- Added `POST /api/v1/returns/{id}/disposition` under location-scoped
  `inventory.adjust`, for both referenced and blind returns. The request names
  a return line, positive quantity (three decimal places), inspection kind,
  reason, note and retry event. Actor and dates come from the server.
- Restock, quarantine, damaged, supplier-return staging and waste all post
  zero-sum `ReturnDisposition` ledger legs. Quarantine also creates one
  identified incident without posting a second inventory entry. Expired
  batches cannot be restocked. Disposition leaves refund entitlement unchanged.
- A line's dispositioned quantity is an optimistic-concurrency token. Immutable
  `SalesReturnDisposition` rows retain each event; changed event replays and
  line overshoots are refused. Stale saves become HTTP 412 and roll back the
  operation. Ledger posting, history, incident and audit share one transaction.
- Migration `20260915181631_SalesReturnDispositions` adds the line counter and
  event table, with PostgreSQL mutation/truncate guards. The EF append-only
  interceptor and deployment grants protect the same history. Apply the
  migration before running the updated API.
- Validation: 873 tests passed with PostgreSQL tests excluded (Domain 364,
  Application 237, Infrastructure 72, Security 52, Architecture 13, API 135).
  The subsequent concurrency-handler regression and all 12 returns endpoint
  tests passed. Migration drift and `git diff --check` passed. Docker is not
  running, so PostgreSQL execution remains unverified. The repository secret
  scanner still reports existing development/test password patterns and the
  key-generation script; this batch adds no credentials.

### 2026-09-16 — C11 customer accounts

- Added optional customer accounts with create, paged search, detail, update,
  deactivate and reactivate routes. Search is case-insensitive across display
  name, phone and email and escapes SQL wildcard characters.
- Split access into offline-capable `customer.view` and `customer.manage`
  permissions. Store Managers and Cashiers can manage and view customers;
  Auditors have read-only access.
- Sale completion now rejects unknown or inactive named customers and persists
  an active customer's ID on the sale. Anonymous and unauthorized callers are
  rejected through the normal API authorization pipeline.
- Customer lifecycle changes use the system clock and write mutation audits.
  Contact details are not copied into audit JSON; deactivation retains its
  required reason. Foreign keys prevent deleting customers referenced by sales
  or returns.
- Migration `20260916015216_CustomerAccounts` creates `sales.customer`, search
  indexes and the optional sale/return foreign keys. Historical free-form
  customer IDs are retained as inactive legacy records before the constraints
  are added, so an existing database can upgrade without losing references.
- Validation: clean build with zero warnings; 892 non-PostgreSQL tests passed
  (Domain 369, Application 241, Infrastructure 79, Security 52, Architecture
  13, API 138). Customer-focused tests contributed 5 domain, 7 repository, 36
  sale-handler and 3 HTTP cases. Migration model drift and `git diff --check`
  passed. Docker is unavailable, so PostgreSQL execution remains unverified.

### 2026-09-16 — C12 discount regressions

- Added HTTP coverage proving an authorized line discount is rechecked at the
  sale location, reduces the payment and persisted net total, and retains the
  authorizing user on the sale item.
- Added the negative path for a caller-supplied authorizer who lacks
  `sale.discount`: the API returns 403 `sale.discount_not_authorized` and leaves
  shelf inventory unchanged.
- Validation: clean integration-test build with zero warnings; all 6 sale
  pipeline tests and all 140 non-PostgreSQL API integration tests passed. With
  the broader C11 run immediately before this test-only batch, the current
  non-PostgreSQL total is 894.

### 2026-09-16 — C13 authenticated web shell

- Replaced the untouched Blazor template with the VaultFlow operations shell.
  The web app now has an API base-address option, typed authentication client,
  circuit-scoped token session and a custom `AuthenticationStateProvider`.
- Added username/password sign-in with an optional two-factor code, user-safe API
  failures, protected routes, safe return navigation and local sign-out. The
  authorization principal carries the API's roles, permissions and locations.
- Added responsive sign-in and workspace screens with visible keyboard focus,
  reduced-motion support and mobile layouts. Removed the template counter and
  weather routes.
- Validation: `Pos.Web` builds with zero warnings. Playwright verified anonymous
  redirect to sign-in, field interaction, API-unavailable feedback, desktop and
  mobile layouts, and no browser console errors. Screenshots are under
  `artifacts/vaultflow-login*.png` and remain untracked.

### 2026-09-16 — C14 API-backed POS cart workspace

- Added the protected new-sale workspace and linked it from the navigation and
  operations overview for users with `sale.create`.
- Store choices come from the locations API and are restricted to the signed-in
  user's assigned stores. Product search and barcode lookup use the catalogue
  API; prices resolve the current store override before the organization-wide
  fallback.
- Added an in-memory cart with duplicate-line quantity increments, quantity
  controls, removal, clear, location locking and Philippine-peso totals. Search
  and scanner inputs update on each keystroke so their actions are immediately
  available.
- Validation: `Pos.Web` builds with zero warnings. A headless Playwright flow
  verified sign-in, assigned-store loading, search, pricing, quantity totals,
  removal, barcode entry and a clean browser console against a temporary API.
  No screenshots or design artifacts were generated for this batch.

### 2026-09-16 — C15 checkout orchestration (web-terminal slice)

- **Terminal slice (backend).** `DevicePlatform.Web` registers a browser as an
  ordinary device, and `Device.ActivateForWeb` marks it Active at registration
  because the signed-in cashier session is the register's credential. A web
  terminal has no offline counter, so the server allocates its document
  numbers: `IDocumentNumberGenerator.NextScopedAsync(documentType, shortCode)`
  mints `SAL-…/RET-…/SHF-…` from the shared counter table keyed by the device
  short code — never from a physical device's own counter, which must stay
  with the device (ADR-0015 / ADR-0032).
- **`/api/v1/terminal` surface** (all `sale.create`, location-scoped via the
  scope source):
  - `GET /registers?locationId=` lists the store's active browser registers as
    `{ id, shortCode, name }`.
  - `GET /session?locationId=` (requires `X-Device-Id`) bootstraps the
    checkout: business date in the store's timezone, VAT rate,
    cash-rounding increment and the shift open on that register. Refuses a
    register that is not active (`device.not_active`), belongs to another
    store (`device.wrong_location`) or is absent (`device.required`).
  - `POST /{locationId}/next-number` with `{ documentType }` allocates the
    next SAL/RET/SHF for the calling register. A physical device is refused
    `409 device.not_web` (its numbers are issued offline and a server-minted
    value would collide); `SHF` additionally checks `shift.open`; invalid
    types get `terminal.document_type_invalid`.
- **Web checkout (`Pos.Web`).** Register picker with auto-select when the
  store has exactly one register; session bootstrap; shift strip (open-shift
  banner or "Start shift" with opening float → SHF next-number + `POST
  /api/v1/shifts/open` + refreshed session); per-line discounts gated on
  `sale.discount`, clamped to the line total; a cash payment panel with quick
  tender buttons (Exact/100/500/1000) and change rounded to the location's
  increment; atomic submission to `POST /api/v1/sales` (SAL next-number +
  `CompleteSaleBody` with the cash payment) and a success panel with reset.
  All register context travels on `X-Device-Id`.
- Validation: 12 new `TerminalEndpointTests` through the real pipeline
  (register list and 403 without location access; session bootstrap with and
  without an open shift; wrong-location and suspended registers refused;
  sequential per-type numbering; SHF `shift.open` gate; invalid document
  type; physical devices refused `device.not_web`). Full API suite **152
  passing** — only the two PostgreSQL-guard members fail, and only because
  Docker is unavailable. Domain 369, Architecture 13, Security 52 green;
  `Pos.Web` builds with zero warnings. Docs: STATUS, ROADMAP, API §9 and
  DECISIONS ADR-0032 updated. This commit also carries the C14 web shell:
  the authenticated operations shell and API-backed cart workspace were
   never committed separately.

### 2026-09-16 — C16 web sale lifecycle

- **Backend.** Three routes through the real pipeline: `GET
  /api/v1/sales?locationId&from&to` (`sale.view`, scoped to the caller's
  assigned locations) returning summaries `{ id, number, status, businessDate,
  completedAtUtc, grossTotal, netTotal }`; `POST /api/v1/sales/{id}/reprint`
  (`sale.reprint`,
  reason ≤ 200 chars, append-only print log); `GET /api/v1/returns/{id}`
  (any of `sale.view | sale.return | sale.refund | inventory.adjust`, full
  return detail with lines, batch info and refund history;
  404 `return.unknown`, 403 `return.outside_scope` via `SalesReturnErrors`).
  Web contracts added: `PosSaleSummary`, `PosSaleDetail`, `PosSaleLineDetail`,
  `PosSalePaymentDetail`, `PosReturnDetail`, `PosReturnLineDetail`,
  `PosReturnRefundDetail`, `PosReference`, and the request records
  (`PosReprintSaleRequest`, `PosVoidSaleRequest`, `PosCreateReturnRequest`,
  `PosCreateReturnLine`, `PosRefundReturnRequest`, `PosDisposeReturnRequest`).
  `VaultFlowApiClient` gains `GetTextAsync`, `SearchSalesAsync`, `GetSaleAsync`,
  `GetSaleReceiptAsync`, `ReprintSaleAsync`, `VoidSaleAsync`, `GetReturnAsync`,
  `CreateReturnAsync`, `RefundReturnAsync`, `DisposeReturnAsync`.
- **`UserSession` register context.** `SetTerminal(PosTerminalSession)` now
  caches the terminal's `TerminalBusinessDate` and `OpenShift` alongside the
  device info, so pages can read register/shift context without re-calling the
  session endpoint. `ClearRegister()` wipes stale register identity on location
  mismatch.
- **`TerminalBar` shared component.** Register auto-select (single register in
  the store), shift strip with "Start shift" flow or open-shift banner,
  stale-register cleared on location mismatch; exposes `OnStateChanged` for
  pages to refresh after register or shift changes. Reused on sale detail,
  return detail and (implicitly) the new sale page.
- **`Sales.razor`** (`/sales`) — store/date-range search page gated by
  `sale.view`; results table with number, time, cashier, total, status;
  row click navigates to `/sales/{id}`.
- **`SaleDetail.razor`** (`/sales/{Id}`) — lines/payments/totals, receipt print
  and reprint with reason, void with reason (blocked without open register and
  shift), accept-return with per-line quantity; `TerminalBar` shown for
  Completed sales when the cashier has reprint/void/return permission; after
  accept-return, navigates to `/returns/{id}`.
- **`ReturnDetail.razor`** (`/returns/{Id}`) — return detail with lines/batch
  info, per-line disposition form (restock/quarantine/damaged/supplier-return/
  waste with reason-code mapping, gated by `inventory.adjust`), refund form
  (method, amount, tendered, provider reference; cash-locked for blind returns,
  gated by `sale.refund`), refund history table, back-to-sale link.
- **`NavMenu`** adds a "Sales" NavLink gated by `sale.view`. **`Home.razor`**
  adds a conditional "Review sales" secondary card for
  `CanViewSales`. **`app.css`** reworked: two-column `command-grid`,
  `primary-card` and `secondary-card` both span full width with distinct
  light backgrounds.
- Validation: 9 new `SaleLifecycleEndpointTests` through the real pipeline —
  sales search by store with the location/date filters honoured and another
  store's Store Manager forbidden; reprint logging the print and the audit
  while keeping the sale Completed, refused without `sale.reprint`, and
  refused at another store; return detail showing the lines and the refund
  history (referenced), a blind return's detail describing the return with no
  sale, an unknown return 404, and another store's Store Manager forbidden the
  detail. Full API
  suite **161 passing** — only the two PostgreSQL-guard members fail (Docker
  unavailable). Domain 369, Application 241 green; `Pos.Web` builds 0 warnings
  0 errors. Docs: STATUS, ROADMAP, API §9 updated.

### C17 — card/e-wallet + split payment checkout (`feat(pos-c17)`)
- `NewSale.razor`: the single cash tendered input is replaced by a payment
  allocation flow. A `List<PaymentInput>` tracks added payments (method, amount,
  optional tendered for cash, optional provider reference). The add-payment form
  presents method tabs (Cash / Card / E-wallet), a precise-money amount input
  seeded with the remaining amount (`MoneyPrecise` = `ToString("0.00########",
  InvariantCulture)`) to avoid rounding-induced payment mismatches at the 4 dp
  compare, a cash tendered field with quick-tender buttons (Exact / 100 / 500 /
  1000), and a provider reference (max 128 chars) for card and e-wallet.
  `Complete` is disabled until `RemainingToPay == 0`. Summary shows amount due,
  allocated, remaining (or estimated change for cash per `RoundToIncrement`).
  No backend changes required: the server-side payment validation (method enum,
  amount > 0, cash tendered ≥ amount, `sale.payment_mismatch` sum check at 4 dp,
  max 128-char provider reference) already supports the full payment mix.
- New CSS: `.allocated-list`, `.allocated-row`, `.method-chip` (`.method-1` /
  `.method-2` / `.method-3` with cash green, card blue, e-wallet purple),
  `.add-payment`, `.method-tabs`, `.quick-tender`, `.add-payment-btn`,
  `.remove-payment`, `.optional`, `.field-hint`. `Pos.Web` builds 0 warnings
  0 errors.
- New `SalePaymentEndpointTests` (6 integration tests): card-only with provider
  reference, e-wallet-only with provider reference, split cash + card (asserts
  both payments, cash change of 5 and card provider reference), split cash +
  e-wallet (asserts total allocated equals net total), under-coverage refused
  409 `sale.payment_mismatch`, over-coverage (cash amount > net) refused 409
  `sale.payment_mismatch`. API suite: **167 passing** (2 known Docker-unavailable
  Postgres failures). Domain 369, Application 241 green. Docs: STATUS,
  PROGRESS, ROADMAP updated.

### C18 — expired-batch sale blocking + authorized override path (`feat(pos-c18)`)
- The §5 contract (`inventory.expired_only`, `sale.expired_override`) was dead
  code: every FEFO shortfall returned generic `inventory.insufficient_stock`.
  `CompleteSaleCommandHandler` now classifies after the sellable allocation
  fails — if the sale's business-date expired shelf can cover the shortfall it
  refuses `inventory.expired_only` (409) so the terminal can offer the exception
  path; a genuine shortfall stays `inventory.insufficient_stock`.
- The exception path carries a mandatory reason per line:
  `CompleteSaleLine.ExpiredOverrideReason` (max 500 chars; empty →
  `sale.expired_override_reason_required`, too long →
  `sale.expired_override_reason_too_long`, both 400). Overriding a line without
  `sale.expired_override` is refused up front `sale.expired_override_denied`
  (403) — a cashier cannot even offer the path.
- `sale.expired.override` audit now records the cashier's reason and the
  authorizing user, which is stamped from the request context
  (`AuthorizingUserId`), never caller-supplied; the audit entry is also
  location-scoped (`LocationId: command.LocationId`, previously null).
- Web terminal (`NewSale.razor`): on submit the server's `inventory.expired_only`
  refusal triggers a confirmation dialog (reason textarea, 500-char limit) gated
  on `sale.expired_override` in the session; confirm re-submits with
  `expiredOverrideReason` per affected line. `Pos.Web` builds 0 warnings 0
  errors.
- Tests: 3 new validator rules + 2 handler refusals/reclassifications +
  reworked audit assertion (camelCase `authorizingUserId`); 4 new
  `ExpiredOverrideEndpointTests` through the real API pipeline — refusal from a
  line whose sellable shelf cannot cover it while the expired shelf could
  (balances untouched), override completion (sellable batch 3→0, expired batch
  5→4, audit row with reason + authorizer), missing reason 400, cashier without
  permission 403.
  Totals: Domain 369, Application 245, Infrastructure 80 (18 skipped),
  Security 52, Architecture 13, API **171 passing** (2 known Docker-unavailable
  Postgres failures). Docs: STATUS, PROGRESS, ROADMAP, POS updated.

### C19 — scheduled-price cancellation (`feat(pos-c19)`)
- **Closes the ADR-0029 gap** ("cancellation of not-yet-effective prices is left
  for POS pricing, Phase 11"). New `Product.CancelScheduledPrice(priceId, nowUtc)`
  in `Product.Curation.cs`:
  - Unknown row → `catalog.price_unknown` (404); a price already in effect → 409
    `catalog.price_already_effective` (past prices are history; change an
    effective price by scheduling a replacement, never by unpicking it).
  - Removing a future price rewinds the schedule exactly as supersession built
    it: the predecessor it superseded carries the old amount through the
    cancelled period — re-closing at the resumption's end, or reopening when the
    base was open-ended (`ProductPrice.Close` is now nullable) — and the
    single continuation row that existed only to restore the old amount is
    removed with it.
  - Cancellation never renumbers someone else's plan: a different amount starting
    exactly where the cancelled price ends → 409 `catalog.price_cancel_successor`;
    a chain of same-amount continuations → 409 `catalog.price_cancel_chain`.
    Both force scheduling a replacement instead.
- API: `POST /api/v1/catalog/products/{id}/prices/{priceId}/cancel` under
  `Permissions.Catalog.ManagePrices` (same as scheduling — a MainInventoryManager
  is 403). Body carries the mandatory reason
  (`catalog.reason_required` / `catalog.reason_too_long`, matching
  `ScheduleProductPriceCommandValidator`). 200 with `{ "id": cancelledPriceId }`.
- Handler audits `product.price.cancelled` with the pre-cancel row as `before`
  and the restored row (when any) as `after` — mirroring `product.price.changed`.
- Tests: **7 new domain** (`ProductCurationTests` — temp+resumption rewind with
  the base reopening, open-ended rewind, gap-filling removal, effective-price
  refusal, different-amount-successor refusal, chain refusal, unknown id) +
  **1 new API integration test** (`ProductStockingEndpointTests` — cancel via
  pipe, base reopens and stays current, `product.price.cancelled` audit row with
  the recorded reason, 403 for the non-price-manager, `price_unknown` 404s).
  Totals: Domain **376**, Application 245, Infrastructure 80 (18 skipped),
  Security 52, Architecture 13, API **172 passing** (2 known Docker-unavailable
  Postgres failures). Docs: STATUS, PROGRESS, ROADMAP, API, DECISIONS updated.

### C20 — thermal and HTML receipt formats (`feat(pos-c20)`)

- `ReceiptRenderer` and `SaleReceiptRenderer` now retain plain text as the
  default while adding a 42-column `Thermal` layout for 80 mm receipt printers
  and a self-contained, escaped `Html` print document sized for 80 mm paper.
- `GET /api/v1/receipts/{id}/print` and `GET /api/v1/sales/{id}/receipt` accept
  `?format=Plain|Thermal|Html`; HTML returns `text/html`, all other formats
  return `text/plain`. Sale receipt first-print logging is unchanged.
- The sale-detail page now offers **Printable / PDF**, which opens the HTML
  document in the browser print dialog.
- Tests: 2 renderer tests (fixed-width output and HTML escaping) plus one API
  integration scenario covering all payment-receipt formats.

### C21 — browser price schedule (`feat(pos-c21)`)

- New `/catalog/prices` workspace: operators can search the active catalogue,
  inspect current, past and scheduled price rows, and see their effective window
  and location scope. Price managers can schedule a global or store-specific
  change in UTC and cancel a future row with a recorded reason.
- The navigation and overview expose the workspace only to `catalog.view` users;
  scheduling and cancellation are separately gated by `product.price.manage`.
- The web client now has typed price-schedule requests. Validation: the product
  stocking/curation integration suite passes all four scenarios; `Pos.Web` and
  the solution build cleanly.

### C23 — notification persistence and expiry alerts (`feat(notifications-c23)`)

- Added `core.notification` as the durable source of truth and
  `core.notification_receipt` for per-user read and acknowledgement state.
  A unique deduplication key prevents a repeated worker pass from raising the
  same operational event twice while still allowing one early warning and one
  critical reminder for a batch nearing expiry.
- The expiry worker now emits location-scoped warnings for every batch inside
  the configured warning window and a critical summary after each successful
  expiry quarantine run. Alert timestamps use the system clock and numeric
  content is culture invariant.
- Added domain, persistence and alert-factory coverage plus migration-ordering
  verification.

### C24 — live notification centre (`feat(notifications-c24)`)

- Added authenticated notification list, mark-read and read-all routes. Every
  query resolves current assignments and `location.all` authority from the
  database; an out-of-scope identifier returns 404 and creates no receipt.
- Added `/hubs/notifications` with per-user and per-location groups. The durable
  writer publishes only after its row commits, and connected Blazor circuits
  automatically reconnect and reload the durable feed.
- Added the web notification ledger, unread filters, read controls, live status,
  navigation count and compact top-bar badge. API integration coverage verifies
  scoping, receipt persistence, anonymous rejection and live SignalR delivery.

### C25 — low-stock alerts (`feat(notifications-c25)`)

- Added `NotificationKind.LowStock` and `ILowStockRepository`. The read sums
  Available balances across batches for active, stocked products at active,
  non-external locations; a reorder point of zero means thresholds were never
  configured and never alerts.
- `LowStockWorker` (section `LowStock`, hourly by default) writes one alert per
  low product: Warning at or below the reorder point, Critical below minimum or
  at zero. The deduplication key carries the level and UTC day, so a product
  that stays low is reminded daily and a worse level alerts immediately.
- Added factory, SQLite repository and worker coverage. No schema change.

### C26 — discrepancy alerts (`feat(notifications-c26)`)

- Added `NotificationKind.ReceivingDiscrepancy` and `TransferShortage`, and
  `IDiscrepancyAlertRepository`, which reads posted goods receipts and received
  transfers inside the lookback window that still have an unresolved
  discrepancy.
- `DiscrepancyAlertWorker` (section `DiscrepancyAlerts`: every 15 minutes, 7-day
  lookback) writes one Warning per receipt at its receiving location, summarising
  unresolved kinds, quantities and value impact, and one Critical per end of a
  short transfer. Each alert references its GRN or transfer. Receipts dedupe on
  the receipt id; transfers dedupe on transfer id plus location.
- A document resolved before the next pass never alerts. Discrepancies older than
  the lookback are not back-filled. Physical-count variances and the
  cost-variance approval notice are not included.
- Added factory and worker unit coverage; the partial-receipt and partial-arrival
  API tests now also assert what the alert repository returns, including that a
  resolved shortage drops out. No schema change.

### C27 — emergency-transfer alerts (`feat(notifications-c27)`)

- Added `NotificationKind.EmergencyTransfer` and an
  `IEmergencyTransferAlertRepository` projection over emergency transfers that
  have a recorded ledger group, so a failed or rolled-back initiation never
  produces an alert.
- `EmergencyTransferAlertWorker` (section `EmergencyTransferAlerts`: every 5
  minutes, 30-day lookback) writes a Critical alert for the source and
  destination store. All-location users see both through the existing scope.
  Each alert references the transfer and deduplicates by transfer plus location.
- Added factory and worker unit coverage, and the real emergency initiation API
  test now verifies the alert projection after the transfer and ledger post
  commit. No schema change.

### C28 — encrypted device database (`feat(offline-c28)`)

- Created `Pos.Client` as a .NET MAUI Blazor Hybrid app targeting Windows and
  Android, with an explicit reference-boundary architecture test.
- Added the dedicated `PosDeviceDbContext`, seven scoped device tables and an
  independent initial SQLite migration. The client opens the database through
  SQLCipher with a random 256-bit key held by platform `SecureStorage`.
- Added device profile, cached product/barcode/price/location/user and
  permission-snapshot entities. Decimal values and UTC timestamps use exact
  text converters; partial unique indexes enforce both global and store-scoped
  permission uniqueness.
- Tests apply the encrypted migration, verify its limited schema and decimal
  representation, prove an unkeyed connection cannot read the file, and cover
  the global permission uniqueness edge case.

### C29 — protected change-feed application (`feat(offline-c29)`)

- `ChangeFeedApplier` is the only writer of `cache_*`, `snapshot_permission` and
  the new `sync_cursor`. It validates a `ChangeFeedPage` whole (cursor and
  sequence order, required fields and column lengths, decimal scale, ISO
  currency, price periods, snapshot expiry and repeated grants), then applies
  every change and the cursor in one `BEGIN IMMEDIATE` transaction. A page whose
  `NextCursor` does not exceed the stored cursor is reported as already applied
  and writes nothing; any other page not starting at the cursor is refused
  `sync.feed_cursor_mismatch`. A snapshot issue replaces the user's whole
  snapshot; a revoke deletes it; a cancelled price is removed.
- Two write guards: an EF interceptor refuses tracked changes to applier-owned
  entities outside the internal write scope, and migration
  `DeviceChangeFeedGuards` installs `BEFORE INSERT/UPDATE/DELETE` triggers that
  call `vf_change_feed_writer()`, registered per connection by the device
  context. Raw SQL and bulk statements are refused, and a keyed connection
  without a device context fails closed.
- 39 new infrastructure tests: every change kind, updates/removals/snapshot
  replacement, in-page ordering, replay, two appliers racing one page, cursor gap, mid-page rollback, 13
  malformed pages, 9 tracked writes, 5 raw statements, bulk statements, a raw
  keyed connection, trigger installation checked against the model, and the
  scope's visibility. Disabling both guards turns exactly the 15 guard-dependent
  tests red.
- CI's migration-drift step named no context and has failed with "More than one
  DbContext" since C28; it now checks `PosDbContext` and `PosDeviceDbContext`
  separately (both verified clean locally).
- Found while testing: each keyed connection open costs 650–800 ms (SQLCipher
  PBKDF2 with pooling disabled), and CI cannot build `Pos.Client` on Ubuntu
  without the MAUI Android workload. Both are recorded in STATUS.md §4.

### C29b — raw-key device keying (`perf(offline-c29b)`)

- `IDeviceDatabaseKeyProvider` now returns the key's 256 bits. The initializer
  refuses any other length before touching the file, clears the array, and
  passes the bits to SQLCipher as a raw key, so opening a connection runs no
  PBKDF2. Measured: 650–800 ms per open as a passphrase, about 2 ms as a raw key.
- The keyed connection string and EF options are built once per process, so the
  platform secure store is read once; a failed read is retried on the next call.
  The secure-storage provider never replaces a stored key that fails to decode,
  since a new key would orphan the encrypted store.
- Initializer pragmas: `cipher_memory_security` (process-wide) and
  `journal_mode = WAL` (stored in the file) stay; `synchronous = NORMAL` and
  `busy_timeout`, which never reached any connection but the discarded
  verification one, are removed. Writes keep the tested `synchronous = FULL`.
- 7 new tests: raw keying (the same bits as a passphrase are refused with
  `SQLITE_NOTADB`), three wrong key lengths, one key read with the handed array
  cleared, a failed read retried, and the pragmas a context connection sees.
  Reverting to passphrase keying or rebuilding the key per call turns the
  matching tests red. The 49 device tests now run in 17 s (C29's 43 took 3 min
  27 s). No device database existed to migrate: nothing is deployed.

### C29c — CI repair (`ci(offline-c29c)`)

- **Diagnosis, reproduced:** in a clean `mcr.microsoft.com/dotnet/sdk:10.0.301`
  Linux container, `dotnet restore VaultFlow.slnx` from a fresh clone fails with
  `NETSDK1147` (needs `maui-android`), exactly as `build-and-test` would.
- **`build-and-test`** now removes `Pos.Client` from its checkout's solution
  first. Verified in the same clean container: restore and Release build
  succeed with 0 warnings; Domain 378, Application 247, Architecture 14,
  Security 52, Infrastructure 147 and API 174 pass. That includes the 49
  SQLCipher device tests, so the encrypted SQLite library works on Linux.
- **`build-client-windows`** (new): Release build of the Windows target. From a
  fresh clone it failed on a real error: `MauiProgram.cs` imported
  `Microsoft.Extensions.Logging` for a call made only in Debug, so Release
  failed on IDE0005. Every earlier build had been Debug. The directive is now
  under `#if DEBUG`; Release and Debug build with 0 warnings. The workload
  install step was not run locally, because it would change this machine's
  Visual Studio-managed workloads.
- **`build-client-android`** (new): pinned workload install verified in a clean
  Ubuntu 24.04 container; `InstallAndroidDependencies` verified on Windows
  against an empty SDK directory. On Linux the container run failed with
  `CommonUtilities.Helpers.UserName must have a valid value` (root, no `USER`
  variable). The Release Android build has not run on Linux yet.
- **Workload pin:** every published set in the 10.0.300 band ships MAUI
  10.0.20, the version `Directory.Packages.props` pins. Set `10.0.301` matches
  `global.json`, and DEPLOYMENT.md §8 says to move the two together.
- **PostgreSQL suites:** with Docker restarted, all 21 passed. A first run under
  heavy Docker load silently skipped the 18 Infrastructure tests; a probe showed
  the container starts fine alone. Recorded as a known gap: the skip check
  treats any start-up failure as "no Docker".
- **Docker Desktop** crashed at start on the known stale socket
  (`Docker/run/dockerInference`); renaming `run` and `docker-secrets-engine`
  to `*.stale-20260917` fixed it again.

### C35 — the ledger on the device (`feat(sync-c35)`)

- **One ledger, two databases.** `InventoryLedger` takes `ILedgerStore`, which
  `PosDbContext` and `PosDeviceDbContext` both implement. ADR-0008 exists because
  two implementations of double-entry stock would drift invisibly; a second
  ledger would have been exactly that. Every existing ledger, balance and API
  test passed unchanged.
- **The device maps the server's own configurations.** `local_inventory_movement`
  and `local_inventory_balance` apply `InventoryMovementConfiguration` and
  `InventoryBalanceConfiguration` verbatim and then rename the tables, so column
  shape, indexes and the concurrency token cannot drift.
- **The guards differ, and the difference is documented.** PostgreSQL's balance
  guard is a *deferred* constraint trigger checking arithmetic at commit. SQLite
  has no deferred triggers — the first version of the device guard failed every
  post, because the ledger's balance insert arrived before its movements — so the
  device trigger asks who is writing, via `vf_ledger_writer()`, the mechanism C29
  proved on the caches. The fourth server layer, a least-privilege role, has no
  SQLite equivalent; the encryption key stands in its place, now stated in
  OFFLINE_SYNC.md §2.2 rather than implied.
- **The write window** is opened by the device unit of work, since a posting
  command stages its rows and leaves the writing to it. It is internal to
  infrastructure, so client code cannot open it. Movement immutability and the
  balance no-delete rule are unconditional.
- **8 tests** against a real encrypted device file: receipt, sale, refusal to
  sell stock that is not there, replay of a repeated event, and three raw-SQL
  attacks (set a quantity, delete a balance, rewrite a movement) all refused.
  Every movement group sums to zero.
- **Verification:** 1,109 passing without PostgreSQL (Domain 378, Application
  247, Infrastructure 233, Security 52, Architecture 25, API 174); no pending
  model changes in either context.

### C34 — the device outbox (`feat(sync-c34)`)

- **`local_outbox_event` and `device_sequence`** (migration `DeviceOutbox`).
  Both are written in the caller's transaction: an event and the rows it
  describes commit together or not at all, a refused command queues nothing, and
  a rolled-back event gives its sequence number back rather than leaving a gap.
- **The repositories enqueue, not the handlers.** An adapter knows which
  business event just happened, and keeping the outbox out of the shared
  handlers is what lets the server keep running the same code without one. What
  is queued is "a shift opened", never "a row changed".
- **`CanonicalJson`** re-emits a payload with object properties sorted by ordinal
  name at every depth, no whitespace, array order untouched. The server compares
  a repeated event identifier against the hash of what it first stored and treats
  a different hash as tampering (ADR-0007); `JsonSerializer` writes properties in
  declaration order, so without this, moving a property on a payload type would
  silently change every hash and turn honest retries into tamper reports.
- **`DeviceUptimeTicks`** is `Environment.TickCount64`, monotonic across a
  wall-clock change, so a device whose clock was moved backwards still produces
  events in an order the server can see through.
- **The status banner reports unsent work**, closing the POS.md §6 gap C32
  recorded. A count, not a queue. `Failed` and `RequiresReview` still count —
  retries are never abandoned — while `Synchronized` and `Conflict` have been
  decided.
- **18 new tests**, including: the sequence is gapless and strictly increasing;
  a rolled-back event releases its number; the two property orders of the same
  content produce identical JSON; opening a shift queues exactly one
  `ShiftOpened` whose payload carries the SHF number and not a table name;
  suspend and resume queue their own events in order; a permission-denied open
  queues nothing.
- **Verification:** 1,101 passing without PostgreSQL (Domain 378, Application
  247, Infrastructure 225, Security 52, Architecture 25, API 174); no pending
  model changes in either context. No PostgreSQL change — the server side of
  sync is the next chunk.

### C33 — the first executable device use case (`feat(offline-c33)`)

- **Two local tables.** `local_cashier_shift` maps the same `CashierShift`
  aggregate the server maps (ADR-0008) — one aggregate, two adapters — and
  `local_audit` is append-only, because the device is the only witness to what
  happened on it while it was offline. Migration `DeviceLocalShiftAndAudit`.
- **The execution substrate**, which every later use case reuses:
  `DeviceUnitOfWork` (the server's contract over the device's SQLite file),
  `DeviceSession` (one drawer, one cashier; grants nothing on its own),
  `DeviceCurrentUser` (no IP, no user agent, no role snapshot, never
  `HasAllLocations` — a device acts at its own location, the same rule the
  permission catalogue states by not marking `location.all` offline-capable),
  `DeviceAuditWriter` and `DeviceNegativeStockAttemptRecorder`.
- **`DeviceShiftRepository`** backs the server's own handlers. Three port
  members answer questions about sales and refuse outright rather than
  improvising, which is precisely why `CloseShiftCommand` stays `Pending`: a
  closing shift that reconciled against a zero it could not verify would balance
  a drawer against a lie.
- **Cached locations carry their settings.** `LocationChanged` gained
  `SettingsJson`, so a device applies its owner's VAT rate, cash rounding,
  negative-stock policy and shift caps. Null reads as `LocationSettings.Default`,
  which the domain already defines as the strictest configuration — a register
  must not become more permissive by losing its connection.
- **Three catalogue entries flip to `Registered`:** open, suspend and resume.
- **8 end-to-end tests** through a container composed the way `MauiProgram`
  composes one, over one encrypted store: the shift opens under
  `SHF-2026-D03-0001`, a number the device minted itself; a second open on the
  same drawer is refused; a number carrying another device's code is refused;
  suspend and resume round-trip; without `shift.open` in the snapshot nothing
  opens; thirteen hours later, snapshot expired, the same command is refused
  again; a close still answers `application.handler_unavailable` rather than
  reaching the repository member that would throw; and a failed open leaves
  neither a shift nor an audit row behind.
- **Verification:** 1,083 passing without PostgreSQL (Domain 378, Application
  247, Infrastructure 207, Security 52, Architecture 25, API 174); no pending
  model changes in either context. `Pos.Client` not compiled locally — no MAUI
  workloads — so its new registrations rest on CI's client jobs.

### C32 — offline status surface (`feat(offline-c32)`)

- **`DeviceStatusView`** in `Pos.Shared` is the contract, and its location is
  the enforcement of "no storage or transport details": `Pos.Shared` references
  nothing, so no field on it could carry the database path, cipher settings, a
  server address or the feed position. A test still checks the produced view
  against the path, the directory, `device.db`, `sqlite`, `cipher`, `http`,
  `cursor` and the raw location and user identifiers, because a string field can
  always be filled in badly.
- **One concern at a time.** `Concern` resolves the earliest state that stops
  the register trading, then what will stop it soon, then the ordinary offline
  case; `Severity` maps that to normal, warning or blocked. Being offline is
  explicitly normal — selling offline is what the device is for — while
  authority expiring inside a shift is a warning. Putting the priority on the
  contract rather than in the component is what makes it testable without
  rendering: 18 of the new tests walk every concern, and one fails if a concern
  is added later without a severity.
- **`DeviceStatusProvider`** never throws for a device that is simply not ready.
  An unopened store, an unenrolled register and nobody signed in are ordinary
  states with something useful to say, and a status screen that threw on them
  would go blank exactly when someone needed to read it. It reports the
  *earliest* expiry in a snapshot, because that is when a user starts losing
  permissions rather than when the last one goes.
- **Connectivity is a port.** `IDeviceConnectivityProbe` is answered in
  `Pos.Client` from MAUI's network access, because reachability is a platform
  question; infrastructure ships `AssumeOfflineConnectivityProbe`, reporting
  offline, which is the safe answer for a device with no adapter wired up.
- **`DeviceStatusBanner`** is in `Pos.SharedUI`, which references only
  `Pos.Shared` — so the component is handed a finished view and has nothing else
  it *could* show. It builds on Linux, unlike `Pos.Client`, so the markup is
  actually compiled here.
- **`DeviceDatabaseInitializer.IsOpen`** lets the status screen ask whether the
  store is open without opening it or catching an exception.
- **Verification:** 1,075 passing without PostgreSQL (Domain 378, Application
  247, Infrastructure 199, Security 52, Architecture 25, API 174); `Pos.Web` and
  `Pos.SharedUI` build clean. `Pos.Client` was not compiled — no MAUI workloads
  in this environment — so `Home.razor`, `NetworkConnectivityProbe` and the new
  registrations rest on CI's client jobs. No migration.

### C31 — device numbering and permission expiry (`feat(offline-c31)`)

- **`document_counter`** is the device's own sequence table and the mirror image
  of the caches: the change feed never writes it, application code must. New
  SQLite migration `DeviceDocumentCounter`; no PostgreSQL change.
- **`DeviceDocumentNumberGenerator`** allocates with the same
  insert-on-conflict-returning upsert the server uses, joining the caller's
  transaction when there is one. It refuses every centrally numbered type, and
  refuses any short code but the enrolled one — minting under another device's
  code would collide with that device's sequence, and the server would accept it
  because the code on the posted number matches a real device.
- **`DeviceSnapshotPermissionEvaluator`** answers from the cached snapshot and
  only narrows: expiry re-checked at every evaluation, grants scoped to their
  location, and the permission required to be offline-capable in the catalogue
  before the database is read.
- **Two guards on the way in.** The page validator refuses a grant naming a
  permission that is not offline-capable or that the client does not know, and
  refuses the whole page. The applier refuses a snapshot whose policy version is
  older than the one held (`sync.snapshot_policy_rollback`); an equal version is
  the ordinary refreshed-expiry re-issue and is applied. `ApplyChangeAsync` now
  returns a `Result`, so a refused change rolls back the changes committed
  before it in the same page.
- **`DeviceDatabaseInitializer.CreateDbContext`** (synchronous) lets the client
  container resolve a scoped context. It throws rather than blocking when the
  database has not been opened: blocking on the key read would block whatever
  scope asked for the context.
- **24 new tests.** Notable ones: twelve parallel allocations produce twelve
  distinct numbers; a rolled-back transaction releases the number it took; a
  year boundary opens a second counter row; a snapshot one second past expiry
  grants nothing. The tamper test had to drop the guard triggers on a raw keyed
  connection first — the C29 triggers refused the straight insert, which is the
  guard working, and the attack they were never meant to stop is exactly what
  the evaluator's catalogue check covers.
- **Every C30 catalogue entry is still `Pending`.** These are two of the ports a
  whitelisted handler needs; the repositories over `local_*` tables are the rest,
  and those tables are not built.
- **Verification:** 1,048 passing without PostgreSQL (Domain 378, Application
  247, Infrastructure 172, Security 52, Architecture 25, API 174). The two
  PostgreSQL API tests fail for want of Docker in this container, which is a
  known gap (STATUS.md §4), not a regression. `Pos.Client` was not compiled
  locally — no MAUI workloads here — so its DI wiring rests on CI's client jobs.

### C30 — client command boundary (`feat(offline-c30)`)

- **`OfflineCommandCatalogue`** is the whitelist: an explicit list of 24 command
  types, one per offline capability in OFFLINE_SYNC.md §1. A command cannot opt
  itself in with an attribute, and the constructor refuses a type that is not an
  `ICommand<>` or a command declared twice.
- **`AddOfflineClientApplication`** is the device composition root, called from
  `MauiProgram`. Same dispatcher, same five behaviours in the same order as the
  server; only the whitelisted commands' handlers and their validators, and no
  query handler — device reads belong to the device database, not to
  server-shaped queries whose repositories address tables a device does not
  carry.
- **Declared is not registered.** Every entry is `Pending`: the device holds
  caches, permission snapshots and the feed cursor, but no `local_*` tables, so
  no handler's repositories can be satisfied there yet. Registering one anyway
  would make the container throw on resolve, where the boundary exists to fail
  closed. Entries flip to `Registered` as the device adapters land.
- **Two cross-checks keep the whitelist honest.** Each entry names the
  permission its command is authorized by, and every one is asserted
  `IsOfflineCapable` in the permission catalogue — the same flag
  `AuthenticationService` uses to trim a device snapshot. A second test reads
  `IAuthorizedMessage.RequiredPermission` off each declared command (through an
  uninitialized instance; none of them reads its own state to answer) and checks
  it against the entry, so the two cannot drift.
- **Handler-level permissions are deliberately unlisted.**
  `sale.expired_override` is not offline-capable, so it can never be in a
  snapshot: the expired-batch override is unreachable offline by construction
  rather than by a check.
- **11 tests** in `Pos.Architecture.Tests`: the catalogue equals the documented
  list (it caught `ReceiveTransferCommand` missing from the expected set on the
  first run), every entry has a handler to register, nothing outside the list
  resolves in the device container, no query handler resolves, the container
  composes nothing outside `Pos.Application`, a server-only command
  (`ApproveStockAdjustmentCommand`) answers `application.handler_unavailable`
  with no ports registered at all, and — through a catalogue the test supplies —
  a registered command resolves, reaches the pipeline, and is refused by the
  authorization behaviour rather than by the dispatcher. Registering every
  handler instead of the whitelisted ones turns 2 of them red.
- **Two capability decisions (2026-09-17), now rows in OFFLINE_SYNC.md §1.**
  C30 found two use cases the permission catalogue allowed offline but the table
  named neither way. Transfer pick, dispatch and verify stay online: a dispatch
  would create stock in transit nobody else can see, and the receiving store
  would count against a transfer the server has never heard of. Customer create
  and edit go offline, because refusing a walk-in an account at the till during
  an outage is the worse failure and the new PII lands in the encrypted device
  store like any other local record; deactivate and reactivate stay online,
  being administrative.
- **Verification (2026-09-18, through C69):** 1,348 passing without PostgreSQL
  (Domain 402, Application 247, Infrastructure 390, Security 52, Architecture 25,
  API 232), and the coverage gate green at 80.4% of lines against a 78% floor; 18 PostgreSQL Infrastructure tests skipped and 2 PostgreSQL API tests
  failing for the same reason — no Docker in the session container.
  `Pos.Client` itself was not compiled here — the MAUI workloads need the
  download host the environment blocks — so its wiring is verified by
  `DeviceCompositionTests` and by CI's client jobs, not locally. No migration; no
  schema change.
