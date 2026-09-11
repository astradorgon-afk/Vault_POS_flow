# Transfer Workflow and State Machine

Covers Main Warehouse → Store, Store → Store, and Store → Main Warehouse
transfers, including chain of custody, discrepancies, and the three transfer
modes (normal, pre-approved, emergency offline).

---

## 1. States

```
Draft
  |  submit
  v
Requested ---------------- reject ----------------> Rejected (terminal)
  |  review
  v
UnderReview
  |  approve / approve-with-modification
  v
Approved
  |  start picking
  v
Picking
  |  picked complete
  v
ReadyForDispatch
  |  dispatch            [LEDGER: Available -> InTransit at source]
  v
Dispatched ------------- cancel dispatch ---------> DispatchCancelled
  |  (implicit)                                     [LEDGER: InTransit -> Available]
  v
InTransit
  |  receive
  +--> PartiallyReceived  (more shipments expected)
  |         |
  v         v
Received                 [LEDGER: InTransit -> destination Available (+ variance leg)]
  |  verify
  v
Verified
  |  no discrepancy                    |  discrepancy raised
  v                                    v
Completed (terminal)              UnderReconciliation
                                       |  resolve all discrepancies
                                       v
                                  Reconciled --> Completed (terminal)

Any pre-dispatch state --- cancel ---> Cancelled (terminal)
EmergencyOffline entry point ---> PendingCentralReview
                                       |  approve            |  reject
                                       v                     v
                                  (re-enters at Received) Rejected/Reversed
```

### 1.1 Permitted transitions

| From | To | Permission | Extra guard |
|---|---|---|---|
| Draft | Requested | `transfer.request` | ≥1 line, qty > 0, source ≠ destination |
| Requested | UnderReview | `transfer.approve` | reviewer scoped to source or HQ |
| Requested | Cancelled | `transfer.request` | requester or HQ, pre-approval only |
| UnderReview | Approved | `transfer.approve` | not the requester; value within approver limit |
| UnderReview | Rejected | `transfer.approve` | reason required |
| Approved | Picking | `transfer.pick` | user scoped to **source** location |
| Picking | ReadyForDispatch | `transfer.pick` | every line picked or explicitly short-picked |
| ReadyForDispatch | Dispatched | `transfer.dispatch` | posts the dispatch movement group |
| Dispatched | DispatchCancelled | `transfer.dispatch` + `transfer.approve` | only before physical departure; reverses ledger |
| InTransit | PartiallyReceived / Received | `transfer.receive` | user scoped to **destination** |
| Received | Verified | `transfer.receive` | verifier ≠ receiver when `RequireSegregatedVerification` |
| Verified | Completed | — | automatic when no open discrepancy |
| Verified | UnderReconciliation | — | automatic when a discrepancy exists |
| UnderReconciliation | Reconciled | `transfer.reconcile` | all discrepancies resolved |
| PendingCentralReview | Received | `transfer.approve` | emergency transfer ratified |
| PendingCentralReview | Rejected | `transfer.approve` | triggers compensating reversal |

Transitions are implemented as a table-driven state machine in
`Pos.Domain.Transfers.TransferStateMachine`, unit-tested exhaustively: for all
(state, trigger) pairs, either a defined target or an explicit rejection.

---

## 2. Ledger effects

Only three transitions touch inventory. Everything else is workflow metadata.

| Transition | Movement group |
|---|---|
| `Dispatch` | `Src/Available −q` → `Src/InTransit +q` (type `TransferDispatch`) |
| `Receive` | `Src/InTransit −qSent` → `Dst/Available +qReceived`, plus `Src/TransitVariance +(qSent − qReceived)` when non-zero (type `TransferReceipt`) |
| `DispatchCancelled` | exact reversal of the dispatch group |

Damaged-on-arrival quantity splits the receipt leg:
`Dst/Available +qGood` and `Dst/Damaged +qDamaged`, which together with the
variance leg still sums to zero against `Src/InTransit −qSent`.

**In-transit stock is held at the source location.** The source retains custody
and the financial exposure until the destination confirms. Reports surface it as
"in transit to <destination>" using `DestinationLocationId` on the movement.

---

## 3. Worked example

Main Warehouse has 500 Coke. Store 1 requests 120; HQ approves 100.

| Step | Main Available | Main InTransit | Main TransitVariance | Store 1 Available |
|---|---|---|---|---|
| Start | 500 | 0 | 0 | 30 |
| Approved (100) | 500 | 0 | 0 | 30 |
| Dispatched | **400** | **100** | 0 | 30 |
| In transit | 400 | 100 | 0 | 30 |
| Received: counted 98 | 400 | **0** | **2** | **128** |
| Discrepancy raised | 400 | 0 | 2 | 128 |
| Resolved: written off | 400 | 0 | **0** | 128 |

At no point does Store 1's Available stock change before the physical count, and
at no point do the two missing units simply disappear from the books.

---

## 4. Chain of custody

`transfer_custody_event` records one row per hand-off:

| `event_kind` | Recorded fields |
|---|---|
| `Requested` | user, device, location, timestamp, notes |
| `Reviewed` | reviewer, decision, modified quantities |
| `Approved` | approver, approved quantities, threshold applied |
| `Picked` | picker, per-line picked quantity, batches selected (FEFO) |
| `Prepared` | packer, carton count, seal number |
| `Dispatched` | dispatcher, carrier, vehicle reference, departure time |
| `InTransitCheckpoint` | optional, transporter scan |
| `Received` | receiver, arrival time, per-line counted quantity |
| `Verified` | verifier, condition notes, photos |
| `Reconciled` | reconciler, resolution per discrepancy |

Each row carries the device and location it was recorded from. The owner UI
renders this as the document timeline described in the brief:

```
TRF-2026-000001
  Requested by  Maria S. (Store 1)      2026-03-04 08:12 (+08)
  Approved by   Jun P.   (Main)         2026-03-04 09:40  qty 120 -> 100
  Picked by     Ana L.   (Main)         2026-03-04 10:05  batch B-2411 (FEFO)
  Dispatched by Ana L.   (Main)         2026-03-04 11:20  vehicle ABC-123 seal 88213
  Received by   Carl D.  (Store 1)      2026-03-04 15:02  counted 98
  Discrepancy                            shortage 2  PHP 90.00  UNDER INVESTIGATION
```

Custody events are append-only and audited like ledger rows.

---

## 5. Discrepancies

`transfer_discrepancy.kind ∈ { Shortage, Overage, Damaged, WrongItem, Expired,
SealBroken, DocumentMismatch }`

Rules:

- A discrepancy is created automatically whenever `quantity_received ≠
  quantity_sent`, or when the receiver flags damage or a wrong item.
- A discrepancy has a value impact computed at the movement's unit cost.
- A transfer cannot reach `Completed` with an open discrepancy.
- Resolutions (`transfer.reconcile`):
  | Resolution | Ledger effect |
  |---|---|
  | `FoundAtSource` | `Src/TransitVariance −q` → `Src/Available +q` |
  | `FoundAtDestination` | `Src/TransitVariance −q` → `Dst/Available +q` |
  | `WriteOffLoss` | `Src/TransitVariance −q` → `EXT-WRITEOFF +q`, reason `Loss` |
  | `WriteOffTheft` | same, reason `Theft`, always notifies the owner |
  | `SourceCountError` | `Src/TransitVariance −q` → `EXT-WRITEOFF +q`, reason `CountCorrection` |
  | `DestinationCountError` | correction posted at destination with reason `CountCorrection` |
- Write-off resolutions above the location's adjustment threshold require the
  higher approval tier, exactly like a stock adjustment.
- Repeated discrepancies on the same route or the same user are surfaced by the
  `ShrinkagePatternWorker` as a dashboard exception ("repeated stock discrepancies").

---

## 6. Store-to-Store transfers

Store managers cannot move stock between stores privately. Every store-to-store
transfer is one of:

1. **Centrally approved (default).** Store 1 requests, HQ reviews and approves,
   Store 2 picks and dispatches, Store 1 receives. HQ sees the whole flow.
2. **Pre-approved.** HQ issues a `pre_approval_token` scoped to a route, product
   set, value cap and expiry. The stores can execute a transfer against it
   without a live HQ decision — including offline. The token is single-use and
   consumed by the transfer that uses it.
3. **Emergency offline** (§7).

The requesting store never has authority over the source store's stock. The
source store must still pick and dispatch; approval alone moves nothing.

---

## 7. Emergency offline transfers

Reality: a store runs out of a staple, the link is down, and goods must move today.

Rules, all enforced:

- Requires **Store Manager** authorization at *both* ends, captured as two
  distinct user authentications on the originating device (`transfer.emergency`).
- Created with `mode = EmergencyOffline` and status `PendingCentralReview`.
- The ledger **is** posted locally (stock genuinely moved, so the books must
  reflect it), but every resulting movement carries
  `server_processing_status = RequiresReview` and the transfer cannot reach
  `Completed`.
- The document number is device-scoped and clearly flagged; UI shows a red
  `EMERGENCY — PENDING CENTRAL REVIEW` banner everywhere the transfer appears.
- On reconnect the events sync like any other, but the server routes them to the
  emergency review queue instead of auto-completing them.
- HQ review outcomes:
  - **Ratify** → transfer moves to `Received`/`Verified` and completes normally.
  - **Ratify with correction** → an adjustment group corrects quantities.
  - **Reject** → a compensating reversal group is posted and an incident is opened.
- Emergency transfers are permanently visible in the owner dashboard's exception
  panel with their review outcome, and are included in a monthly control report.
- A configurable cap limits emergency transfers per store per month; exceeding it
  blocks new emergency transfers until HQ resets the counter.

There is **no** path by which an emergency transfer silently looks like a normal one.

---

## 8. Replenishment recommendations

A read-model job computes, per `(product, location)`:

```
deficit(loc)  = max(0, TargetStock(loc)  - Sellable(loc))
excess(loc)   = max(0, Sellable(loc)     - TargetStock(loc))
urgency(loc)  = Sellable(loc) < MinimumStock ? Critical
              : Sellable(loc) <= ReorderPoint ? High : Normal
```

Recommendations are produced by matching deficits against excess, preferring:
1. the Main Warehouse as source when it has excess above its own target;
2. otherwise the store with the largest excess, tie-broken by shortest route;
3. never a source that would fall below its own `MinimumStock`.

Worked example from the brief:

| Location | Current rice | Min | Target | Deficit | Excess |
|---|---|---|---|---|---|
| Store 1 | 10 | 25 | 80 | 70 | — |
| Store 2 | 150 | 25 | 50 | — | 100 |

Recommendation: transfer **min(deficit, excess, PreferredReplenishmentQuantity)**
→ e.g. 30 rice, Store 2 → Store 1, urgency `Critical`.

Recommendations are **suggestions only**. They create a `Draft` transfer when a
user accepts one. Automatic execution is off by default and gated behind an
organization setting plus `transfer.auto_replenish`; even then it only creates a
`Requested` transfer, never an approved one.
