# Offline Operation and Synchronization

The design rule: **a device is an event producer, not a database replica.**
Nothing is table-synced. The client uploads immutable business events and
downloads a scoped, versioned change feed.

---

## 1. What a device can do offline

| Capability | Offline | Notes |
|---|---|---|
| Product lookup / barcode scan | yes | from cached catalog |
| Price lookup | yes | cached effective-dated prices for the device's location |
| POS sale (cash) | yes | full ledger posting locally |
| POS sale (card) | no | requires the payment provider; cash-only fallback |
| Receipt print / reprint | yes | reprint still requires `sale.reprint` |
| Local sales history (current + prior shifts held locally) | yes | 90-day local retention |
| Open / close shift | yes | shift totals reconciled on sync |
| Void (same shift, same day) | yes | requires `sale.void` in the cached snapshot |
| Customer return referencing a **local** sale | yes | goods land in `ReturnPending` |
| Customer return referencing a **remote** sale | no | needs server lookup; queued as a request |
| Stock count entry | yes | submitted for approval; **never posts to the ledger offline** |
| Receiving against a **pre-authorized** transfer or PO | yes | authorization token cached |
| Receiving anything else | quarantine only | raises an incident |
| Unknown barcode | quarantine only | raises an incident; never becomes Available |
| Transfer request | yes | queued as `Requested`, approved centrally later |
| Transfer approval | **no** | except via a valid pre-approval token |
| Transfer pick / dispatch / verify | **no** | the source side of a transfer stays online, so stock in transit is never created from a device |
| Emergency transfer | yes | `PendingCentralReview`, dual manager auth |
| Stock adjustment | create only | approval never happens offline |
| Customer record create / edit | yes | a walk-in account opened at the till; new PII originates on the device and syncs up |
| Customer deactivate / reactivate | no | administrative, and it can wait for the link |
| Product creation / price change | **no** | central authority only |
| Reports beyond the local location and retention window | no | |

The rule behind the table: **offline never widens authority.** If an action
required approval online, it still requires approval offline — it just gets
parked in a reviewable state instead of being blocked outright when the goods
have physically moved.

Two rows were added on 2026-09-17, after C30 found that the permissions allowed
both but the table named neither. Transfer pick, dispatch and verify are
offline-capable permissions, but a device may not use them: a dispatch creates
stock in transit that no one else can see until the device syncs, and the
receiving store would be counting against a transfer the server has never heard
of. Creating and editing a customer is allowed, because the alternative is
refusing a customer at the till during an outage; deactivation is not, because
nothing at a till depends on it.

---

## 2. Local data model

```
device.db (SQLite, encrypted)
├── cache_*        master data mirrors, written only by the sync downloader
│    product, product_barcode, product_price, uom, unit_conversion,
│    location, supplier_lite, customer_lite, tax_code
├── snapshot_permission   user -> permission set, policy_version, expires_at_utc
├── snapshot_token        pre-approval tokens (signed, scoped, expiring)
├── local_*        authoritative-until-synced local records
│    cashier_shift, audit, inventory_movement,
│    inventory_balance, outbox_event                    (built, C33–C35)
│    sale, sale_item, payment, sales_return,
│    transfer_order (local), quarantine_incident,
│    inventory_count                                    (not built yet)
├── document_counter  the device's own SAL/RET/SHF sequences
├── outbox_event   the upload queue
├── sync_cursor    feed positions, advanced with the page they follow
└── sync_state     checkpoints, failures
```

The `local_*` tables and `document_counter` are the mirror image of the cache
tables: the change feed never
writes it, and application code must. It is what lets a sale rung up with no
network keep the number printed on its receipt. The allocation is one atomic
upsert that joins the caller's transaction, so a sale that rolls back releases
its number instead of leaving a gap, and a device allocates only under its own
enrolled short code — minting under another device's would collide with that
device's sequence and the server would accept it, because the code on the posted
number would match a real device.

`cache_*`, `snapshot_permission` and `sync_cursor` are **read-only to application
code**; the only writer is `ChangeFeedApplier`. Two independent guards enforce it:

- An EF interceptor refuses tracked inserts, updates and deletes of those
  entities outside the applier's write scope, before any SQL is sent.
- `BEFORE INSERT/UPDATE/DELETE` triggers on each table call
  `vf_change_feed_writer()`. The device context registers that function on every
  connection it opens, and it returns 1 only while that context's write scope is
  open, so raw SQL and `ExecuteUpdate`/`ExecuteDelete` are refused too. A keyed
  connection opened outside a device context has no such function, so its
  writes fail closed.

The write scope is internal to `Pos.Infrastructure`, so client code cannot open
it. The guards stop application code from widening its own permissions or
rewriting cached prices; they do not defend against code holding the database
key, which could drop the triggers.

### 2.2 The ledger on a device

The device runs **the same** `InventoryLedger` the server runs, pointed at its
encrypted SQLite file through `ILedgerStore` (ADR-0008). There is no second
ledger: two implementations of double-entry stock would drift, and the drift
would be invisible until inventory disagreed.

Of the four layers that stop `product.StockQuantity = 100`, three carry to the
device and one cannot:

| Layer | On the server | On the device |
|---|---|---|
| Domain types with no setters | yes | the same types |
| EF interceptor | yes | yes |
| Database triggers | arithmetic: a **deferred** constraint trigger checks at commit that every balance change is backed by movements in the same transaction | identity: SQLite has no deferred triggers, so a `BEFORE` trigger cannot see movements EF has not inserted yet. It asks *who* is writing instead, through `vf_ledger_writer()` — the mechanism that already protects the downloaded caches |
| Least-privilege database role | the application role holds only `SELECT, INSERT` on the ledger tables | **no SQLite equivalent.** What stands in its place is the encryption key, which is why it lives in the platform secure store and never in the file |

The ledger identifies itself by opening a write window (`BeginLedgerWrite`). On
the server that handle does nothing. On the device it is opened by the unit of
work, because a command that posted to the ledger stages its rows and leaves the
writing to the unit of work; the window is internal to infrastructure, so client
code cannot open it. A connection opened outside a device context has no
`vf_ledger_writer()` at all, so a statement touching a balance cannot even be
prepared — the guard fails closed.

Movement immutability and the balance no-delete rule need no window: they are
refused unconditionally, exactly as on the server.

---

### 2.1 The outbox

```csharp
public sealed class OutboxEvent
{
    public Guid    EventId          { get; init; }  // UUIDv7, generated once, never regenerated
    public long    DeviceSequence   { get; init; }  // strictly monotonic per device
    public SyncEventType Type       { get; init; }
    public string  PayloadJson      { get; init; }  // canonical JSON, stable property order
    public byte[]  PayloadHash      { get; init; }  // SHA-256 of PayloadJson
    public Guid    DeviceId         { get; init; }
    public Guid    UserId           { get; init; }
    public Guid    LocationId       { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; }   // device clock at creation
    public long    DeviceUptimeTicks{ get; init; }       // monotonic, clock-tamper evidence
    public Guid    CorrelationId    { get; init; }
    public OutboxStatus Status      { get; set; }   // Pending|Sending|Synchronized|Failed|RequiresReview|Conflict
    public int     AttemptCount     { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? NextRetryAtUtc   { get; set; }
    public string? LastError        { get; set; }
    public string? ServerResponseJson { get; set; }
}
```

`EventId` is created **once**, when the business event is created, inside the
same local transaction as the local business rows. It is never regenerated on
retry — that is what makes retries safe.

`DeviceSequence` comes from a single-row counter table incremented in the same
transaction, giving a gapless per-device order.

**Built in C34.** The device repositories enqueue, not the handlers: an adapter
knows which business event just happened, and keeping the outbox out of the
shared handlers is what lets the server run the same code without one. What is
queued is "a shift opened", never "a row changed".

`PayloadJson` is written by `CanonicalJson`, which re-emits the payload with
object properties sorted by ordinal name at every depth and no whitespace. That
matters because the server compares a repeated event identifier against the hash
of what it first stored and treats a different hash as tampering (ADR-0007):
`JsonSerializer` writes properties in declaration order, so moving a property on
a payload type would otherwise change every hash and turn honest retries into
tamper reports. Array order is left alone — it is data, not layout.

`DeviceUptimeTicks` is `Environment.TickCount64`, which keeps increasing across a
wall-clock change, so a device whose clock was moved backwards still produces
events in an order the server can see through.

An event and the rows it describes commit together or not at all: a command the
device refused queues nothing, and a rolled-back event gives its sequence number
back rather than leaving a gap.

---

## 3. Upload protocol

```
POST /api/sync/push
Authorization: Bearer <access token>          (device-bound)
X-Device-Id: <guid>
X-Correlation-Id: <guid>
Idempotency-Key: <batch guid>

{
  "deviceId": "...",
  "batchId": "...",
  "clientSentAtUtc": "2026-03-04T07:11:52.113Z",
  "deviceUptimeTicks": 918273645,
  "events": [
    { "eventId": "...", "deviceSequence": 4412, "type": "SaleCompleted",
      "occurredAtUtc": "...", "payload": { ... }, "payloadHash": "base64" }
  ]
}
```

Response — **one result per event**, never a single batch-level verdict:

```json
{
  "serverReceivedAtUtc": "2026-03-04T07:11:52.640Z",
  "clockSkewSeconds": 3.1,
  "results": [
    { "eventId": "...", "outcome": "Accepted",
      "serverDocumentNumber": "SAL-2026-D03-000812",
      "appliedAtUtc": "..." },
    { "eventId": "...", "outcome": "Duplicate", "originalAppliedAtUtc": "..." },
    { "eventId": "...", "outcome": "Rejected",
      "errorCode": "product.disabled",
      "message": "Product was disabled on 2026-03-01",
      "remediation": "QuarantineAndReview" },
    { "eventId": "...", "outcome": "RequiresReview", "reviewQueue": "EmergencyTransfers" },
    { "eventId": "...", "outcome": "Conflict", "conflictKind": "TransferAlreadyReceived" }
  ],
  "nextCursor": 1902334
}
```

### 3.1 Server processing algorithm

For each event, in `deviceSequence` order, in its **own** database transaction:

```
BEGIN
  -- 1. idempotency
  SELECT outcome, result_json FROM sync.processed_event WHERE event_id = @id FOR UPDATE
  IF FOUND:
      IF payload_hash <> stored_hash:  record SyncFailure('idempotency-key-reuse'), return Rejected
      ELSE: return stored result unchanged        -- exactly-once
  -- 2. ordering
  IF @deviceSequence <> checkpoint.last_accepted + 1:
      IF @deviceSequence <= checkpoint.last_accepted: return Duplicate
      ELSE: buffer the event, return Deferred     -- a gap; wait for the missing one
  -- 3. device + user authorization, evaluated NOW (not as of event creation)
  -- 4. domain validation against current server state
  -- 5. apply the business effect (ledger post, sale insert, ...)
  -- 6. INSERT sync.processed_event (event_id, hash, outcome, result_json)   <- same tx
  -- 7. INSERT audit.audit_log
  -- 8. advance sync.sync_checkpoint.last_accepted_device_sequence
COMMIT
```

Because step 6 shares the transaction with step 5, there is no window in which
the effect is committed but the idempotency record is not. A device that retries
after a network timeout receives the original result, with the original document
number, and posts nothing twice.

### 3.2 Retry strategy

Client-side, per event:

```
attempt 1 : immediately
attempt n : delay = min(2^(n-1) * 5s, 30 min) * jitter(0.8 .. 1.2)
after 8 consecutive failures -> Status = Failed, surfaced in the UI, kept forever
```

Retries are **never** abandoned automatically; a permanently failing event is
escalated to HQ as a `SyncFailure` with the full server response. Events are
uploaded in batches of at most 100 or 512 KB, whichever comes first, always in
`deviceSequence` order, and a batch stops at the first `Deferred`.

---

## 4. Executing the same code on both sides

`Pos.Client` registers a restricted command set against a SQLite
`PosDbContext`. The sale handler, the ledger, the FEFO allocator and the money
arithmetic are **the same types** the server runs against PostgreSQL. The
differences are injected, not branched:

| Port | Server implementation | Client implementation |
|---|---|---|
| `IDocumentNumberGenerator` | central counter table | device-scoped counter |
| `IPermissionEvaluator` | live DB + cache | cached snapshot (+ expiry check) |
| `IApprovalGate` | resolves approvers, may block | token check, else `PendingCentralReview` |
| `IClock` | server UTC | device UTC + recorded skew |
| `IChangeFeedPublisher` | writes `change_log` | writes `outbox_event` |
| `IPriceResolver` | live effective-dated prices | cached prices + `PriceVersion` stamp |

The whitelist is `OfflineCommandCatalogue`: an explicit list of command types,
not an attribute a command can put on itself, so a new use case is
offline-incapable until someone adds it there and updates the boundary tests.
`AddOfflineClientApplication` composes the device container from it — the same
dispatcher and the same behaviours in the same order as the server, but only the
listed commands' handlers and validators, and no query handler. A command not in
the whitelist simply has no registered handler, so an offline attempt fails
closed with `application.handler_unavailable` (`Unavailable`), never by silently
degrading.

Each entry names the permission the pipeline authorizes it by, and those are
asserted to be offline-capable in the permission catalogue — the same flag that
trims a device's snapshot. The two lists therefore cannot disagree: a command
whose permission a device may not cache cannot be declared offline-capable.
Permissions a handler checks for itself are not listed and do not need to be;
`sale.expired_override` is not offline-capable, so it can never reach a snapshot
and the expired-batch override is unreachable offline by construction.

An entry is `Pending` until the device-side ports its handler needs exist, and
only a `Registered` entry is added to the container. As of C33 the shift
lifecycle — open, suspend, resume — is `Registered` and executes on a device;
everything else is still `Pending` for want of local sale and movement tables.
Closing a shift stays `Pending` on purpose: it reconciles the drawer against the
shift's cash sales, and balancing against a figure the device cannot read would
be worse than refusing. The distinction is not
bookkeeping: registering a handler whose repositories are unregistered would
make the container throw on resolve, where the whole point of the boundary is to
fail closed with a result the UI can explain.

---

## 5. Download: the change feed

```
GET /api/sync/pull?cursor=1902334&limit=500
```

Server returns changes with `change_sequence > cursor` where
`location_scope_id IS NULL` (global master data) **or** equals the device's
location, ordered by `change_sequence`. The feed carries:

- catalog changes (products, barcodes, prices, conversions, settings),
- location and supplier changes,
- the device's own document acknowledgements and server-side corrections,
- transfers and POs addressed to the device's location,
- notifications targeted at the device's location or its users,
- permission snapshot invalidations,
- pre-approval tokens issued to the location,
- device directives (`revoke`, `force-resync`, `purge-cache`).

The client applies a page and advances its cursor **in the same** SQLite
transaction (`BEGIN IMMEDIATE`, so a second applier waits and then sees the new
cursor). An interrupted pull leaves no trace. A replayed page, one whose commit
the client never observed, is recognised because its `nextCursor` does not
exceed the stored cursor, and nothing is written. A page that does not continue
from the stored cursor is refused with `sync.feed_cursor_mismatch`; a malformed
page is refused whole, before anything is written, with `sync.feed_page_invalid`.
Changes are saved one at a time inside the transaction, so a later change always
sees an earlier one to the same row exactly as the server ordered them.

Full re-baseline: when `master_data_version` on the server exceeds the device's
by more than the retained feed window (or the device has been offline beyond
`FeedRetentionDays`, default 30), the server answers with
`410 Gone { "action": "rebaseline" }` and the device downloads a fresh snapshot
from `/api/sync/baseline`. Its outbox is preserved and uploaded first.

---

## 6. Clocks

- The device clock is **never** trusted for ordering, business dates, pricing
  validity, token expiry, or audit timestamps.
- Every event carries the device's `occurredAtUtc` plus `deviceUptimeTicks`
  (monotonic). The server stores `occurred_at_utc` as reported and
  `recorded_at_utc` from its own clock; `recorded_at_utc` orders the ledger.
- Skew is computed per batch. `|skew| > 120 s` raises a warning notification;
  `> 15 min` marks subsequent events `RequiresReview` and prompts the device to
  resynchronise its clock.
- Backwards jumps in `deviceUptimeTicks` relative to `occurredAtUtc` are
  clock-tamper evidence and are audited.
- `business_date` is assigned by the server from the **location's** timezone
  applied to `recorded_at_utc`, unless the event was created offline, in which
  case the device's reported date is used **if** it falls within the shift's
  open window; otherwise the shift's business date wins.

---

## 7. Conflict rules

Generic last-write-wins is **never** used for inventory or money. Each scenario
has a defined rule:

| Scenario | Rule |
|---|---|
| Same event uploaded twice | Idempotency: return the stored result. No second effect. |
| Same event, different payload hash | `Rejected` + `SyncFailure('idempotency-key-reuse')` + security alert. |
| Device offline for days, then floods events | Accepted in sequence order; each validated against *current* server state; business dates preserved. |
| Product changed while device offline (name, category) | Server state wins for master data; the sale keeps the **historical** name/price it printed, stored on `sale_item`. |
| Price changed while device offline | Sale is accepted at the price actually charged; a `PriceVarianceRecorded` note is attached when it differs from the server's effective price, and it appears on the price-variance report. No silent re-pricing. |
| Product disabled while device offline | Sale **accepted** (goods left the shelf, the ledger must reflect reality) but flagged `RequiresReview`; the product stays disabled and no further sales are possible once the feed reaches the device. |
| Product deleted | Impossible — products are never deleted, only deactivated. |
| User permission reduced while offline | Evaluated at processing time: if the user lacks the permission **now**, the event is `RequiresReview` (not silently accepted, not destroyed). Cash-sale events are always accepted and flagged, because the money already changed hands. |
| User disabled while offline | Same as above, plus a security alert; the shift is force-closed on the server. |
| Transfer received quantity ≠ dispatched | Not a conflict — a `TransferDiscrepancy` with a `TransitVariance` ledger leg. |
| Transfer already received by another device | `Conflict: TransferAlreadyReceived`; the second receipt is rejected and surfaced for manual reconciliation. |
| Two locations act on the same stock | Impossible at the data level: buckets are location-keyed. Cross-location races resolve at the transfer boundary. |
| Local sale drove stock negative | Accepted per the location's negative-stock policy; if `Prohibit`, the sale is `RequiresReview` and a `NegativeStockAttempt` is recorded. The sale is never deleted. |
| Events arrive out of order | Sequence gap ⇒ later events buffered until the gap closes or `GapTimeout` (default 30 min) ⇒ then `RequiresReview`. |
| Event references stale master data (unknown product id) | `Rejected` with `remediation: QuarantineAndReview`; the device converts it to a quarantine incident. |
| Duplicate document number from a re-imaged device | Rejected — `sale.number` is unique; the device is forced to rebaseline and re-number pending events. |

Guiding principle, applied consistently: **physical reality is recorded; authority
is re-verified; anything questionable is flagged, never discarded and never
silently accepted as normal.**

---

## 8. Sync statuses

Local (`OutboxStatus`): `Pending`, `Sending`, `Synchronized`, `Failed`,
`RequiresReview`, `Conflict`.

Server (`ServerProcessingStatus` on movements and documents): `Accepted`,
`Duplicate`, `Rejected`, `RequiresReview`, `Conflict`, `Reversed`.

Both are shown in the client status bar and in the HQ sync-health dashboard:

```
[ ONLINE ]  Store 1 · D03 · Cashier: Maria S. · Shift SHF-2026-D03-0042 open
            Sync: 0 pending · last 12s ago            [ OFFLINE ] 14 pending · retry in 2m
```

---

## 9. Security of the sync channel

- TLS 1.2+ only; certificate pinning on the client against the API's issuer.
- Access tokens are device-bound: the `device_id` claim must match the
  `X-Device-Id` header and the device's status must be `Active`.
- Replay protection = idempotency (`event_id` unique) + per-device monotonic
  sequence + batch `Idempotency-Key` + server-side clock-skew bounds. A replayed
  batch produces only `Duplicate` results.
- Envelope integrity: `payloadHash` is verified server-side; a mismatch between a
  re-sent `event_id` and its stored hash is treated as tampering.
- Revoked or suspended devices receive `403` with `{"action":"wipe-local-cache"}`;
  the client purges `cache_*` and `snapshot_*` but **retains the outbox** so
  pending sales are not lost, and displays a lock screen.
- Rate limits: 60 push batches/minute/device, 600 pull requests/hour/device.
- Payloads are never logged in full; the logger emits event type, id, sequence,
  hash and outcome only.

---

## 10. Test matrix (`Pos.Sync.Tests`)

| Test | Asserts |
|---|---|
| `DuplicateUpload_ReturnsStoredResult_AndPostsOnce` | one sale, one movement group |
| `RetryAfterTimeout_DoesNotDoublePost` | simulated commit-then-drop response |
| `OutOfOrderEvents_AreBufferedThenApplied` | seq 5 before 4 |
| `SequenceGapTimeout_MarksRequiresReview` | gap never closes |
| `DeviceOffline7Days_UploadsInOrder_PreservesBusinessDates` | batch of 900 events |
| `DisabledProduct_SaleAccepted_ButFlagged` | conflict rule |
| `ReducedPermission_TransferApproval_Rejected` | offline approval attempt |
| `RevokedDevice_PushRejected_OutboxPreserved` | 403 + wipe directive |
| `TamperedPayloadWithSameEventId_Rejected` | hash mismatch |
| `EmergencyTransfer_RoutedToReviewQueue_NotAutoCompleted` | emergency path |
| `FailedTransaction_RollsBackEntirely` | ledger + sale + processed_event |
| `Rebaseline_PreservesOutbox` | 410 handling |
| `ClockSkew20Minutes_FlagsEvents` | skew policy |
