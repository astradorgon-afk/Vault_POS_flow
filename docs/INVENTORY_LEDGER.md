# Inventory Movement Ledger

The single source of truth for "how much of what is where, and why".

---

## 1. Principles

1. **Append-only.** An `InventoryMovement` row, once committed, is never updated
   and never deleted. There is no `UPDATE` or `DELETE` statement against the
   table in application code, and the database denies both (see §8).
2. **Double-entry.** Every inventory event produces **two or more legs** whose
   signed quantities sum to exactly zero. Stock is never created or destroyed —
   it moves between buckets, and buckets include external counterparties.
3. **Balances are derived.** `InventoryBalance` is an optimisation. It can be
   dropped and rebuilt from the ledger at any time and must always agree with it.
4. **Every leg is authorized.** A leg records the user who created it, the
   approver where an approval was required, the device, and the reference
   document that justified it.
5. **Corrections are new events.** A mistake is fixed by a *reversal* movement
   group that points at the original, plus (if needed) a correcting group.

---

## 2. The Bucket

Inventory is keyed by a four-part bucket:

```
(LocationId, ProductId, BatchId?, InventoryState)
```

A `Product` therefore has no single stock number. The example from the brief:

| Location | State | Quantity |
|---|---|---|
| Main Warehouse | Available | 300 |
| Store 1 | Available | 100 |
| Store 2 | Available | 75 |
| Store 3 | Available | 25 |
| **Total business (internal locations)** | | **500** |

`BatchId` is `NULL` for products with `TracksBatches = false`. For batch-tracked
products every leg **must** carry a `BatchId`; this is a check constraint, not a
convention.

### 2.1 Inventory states

| State | Sellable | Meaning |
|---|---|---|
| `Available` | yes | Free stock at the location |
| `Reserved` | no | Committed to an open sale/transfer, still physically present |
| `InTransit` | no | Dispatched, custody retained by the **source** location |
| `Quarantine` | no | Unverified / unauthorized goods awaiting Main Warehouse review |
| `PendingInspection` | no | Received goods or returns awaiting inspection |
| `ReturnPending` | no | Customer return accepted, disposition not yet decided |
| `Damaged` | no | Physically unsellable, awaiting disposal or supplier return |
| `Expired` | no | Past expiry, blocked from sale, awaiting disposal |
| `TransitVariance` | no | Shipment shortfall held at source pending investigation |
| `External` | n/a | Only valid on virtual locations (supplier/customer/write-off) |

`TransitVariance` is an addition beyond the states listed in the brief. It exists
so a shipment shortfall stays balanced and *visible* instead of vanishing — see
§6.3 and ADR-0006.

### 2.2 Virtual locations

Three system locations of `LocationKind.External` exist so the ledger is closed:

| Code | Purpose |
|---|---|
| `EXT-SUPPLIER` | Counterparty for supplier receipts and supplier returns |
| `EXT-CUSTOMER` | Counterparty for POS sales and customer returns |
| `EXT-WRITEOFF` | Counterparty for disposal, loss, theft, spoilage write-offs |

They hold state `External`, carry negative balances (stock that has come *from*
a supplier, gone *to* a customer), are excluded from all "business inventory"
reporting, and cannot be selected as a transfer source or destination in the UI.

The payoff: a single global integrity assertion.

```sql
SELECT SUM(quantity_delta) FROM inventory_movement;  -- must always be 0
```

---

## 3. The Movement Record

```csharp
public sealed class InventoryMovement          // append-only
{
    public Guid    Id                    { get; }  // server-assigned, UUIDv7
    public Guid    EventId               { get; }  // globally unique business event
    public Guid    MovementGroupId       { get; }  // all legs of one event
    public short   LegNumber             { get; }  // 1..n within the group

    public Guid    ProductId             { get; }
    public Guid?   BatchId               { get; }
    public Guid    LocationId            { get; }  // the bucket affected
    public InventoryState State          { get; }  // the bucket affected

    public decimal QuantityDelta         { get; }  // signed, never zero
    public decimal UnitCost              { get; }  // >= 0, valuation cost
    public decimal TotalValueDelta       { get; }  // = UnitCost * QuantityDelta

    public InventoryMovementType MovementType { get; }

    // Event context: describes the whole event, identical on every leg.
    public Guid?   SourceLocationId      { get; }
    public Guid?   DestinationLocationId { get; }
    public InventoryState? SourceState   { get; }
    public InventoryState? DestinationState { get; }

    public ReferenceDocumentType ReferenceDocumentType { get; }
    public Guid?   ReferenceDocumentId   { get; }
    public string  ReferenceNumber       { get; }  // e.g. TRF-2026-000001

    public Guid    CreatedByUserId       { get; }
    public Guid?   ApprovedByUserId      { get; }
    public Guid?   DeviceId              { get; }

    public DateTimeOffset OccurredAtUtc  { get; }  // business time (device-reported, validated)
    public DateTimeOffset RecordedAtUtc  { get; }  // server clock — authoritative ordering
    public DateOnly BusinessDate         { get; }  // location-local trading day

    public AdjustmentReasonCode? ReasonCode { get; }
    public string? Notes                 { get; }

    public Guid?   ReversesMovementGroupId { get; } // set on reversal legs
    public SyncStatus SyncStatus         { get; }
    public ServerProcessingStatus ServerProcessingStatus { get; }
    public Guid    CorrelationId         { get; }
    public long    ChangeSequence        { get; }  // monotonic, for the change feed
}
```

### 3.1 Movement types

| Type | Typical legs |
|---|---|
| `SupplierReceipt` | `EXT-SUPPLIER/External -q` → `Main/PendingInspection +q` or `Available +q` |
| `SupplierReturn` | `Loc/Damaged -q` → `EXT-SUPPLIER/External +q` |
| `TransferDispatch` | `Src/Available -q` → `Src/InTransit +q` |
| `TransferReceipt` | `Src/InTransit -q` → `Dst/Available +q` (+ variance leg) |
| `TransferCancelDispatch` | `Src/InTransit -q` → `Src/Available +q` |
| `PosSale` | `Store/Available -q` → `EXT-CUSTOMER/External +q` |
| `PosSaleVoid` | exact reversal group of the sale |
| `CustomerReturn` | `EXT-CUSTOMER/External -q` → `Store/ReturnPending +q` |
| `ReturnDisposition` | `Store/ReturnPending -q` → `Available` / `Damaged` / `Quarantine` / `EXT-WRITEOFF` |
| `Reservation` | `Loc/Available -q` → `Loc/Reserved +q` |
| `ReservationRelease` | `Loc/Reserved -q` → `Loc/Available +q` |
| `QuarantineEntry` | `EXT-SUPPLIER/External -q` → `Loc/Quarantine +q` (unauthorized delivery) |
| `QuarantineRelease` | `Loc/Quarantine -q` → `Loc/Available +q` |
| `QuarantineReject` | `Loc/Quarantine -q` → `EXT-SUPPLIER/External +q` (returned) |
| `InspectionPass` | `Loc/PendingInspection -q` → `Loc/Available +q` |
| `InspectionFail` | `Loc/PendingInspection -q` → `Loc/Damaged +q` |
| `Damage` / `Spoilage` / `Loss` / `Theft` / `Breakage` / `Contamination` | `Loc/<state> -q` → `EXT-WRITEOFF/External +q` |
| `ExpiryQuarantine` | `Loc/Available -q` → `Loc/Expired +q` |
| `ExpiryWriteOff` | `Loc/Expired -q` → `EXT-WRITEOFF/External +q` |
| `CountAdjustmentIncrease` | `EXT-WRITEOFF/External -q` → `Loc/Available +q` |
| `CountAdjustmentDecrease` | `Loc/Available -q` → `EXT-WRITEOFF/External +q` |
| `ApprovedStockAdjustment` | as above, with an explicit approver and reason |
| `TransitVarianceResolveFound` | `Src/TransitVariance -q` → `Dst/Available +q` |
| `TransitVarianceWriteOff` | `Src/TransitVariance -q` → `EXT-WRITEOFF/External +q` |
| `OpeningBalance` | `EXT-WRITEOFF/External -q` → `Loc/Available +q` (commissioning only) |

`EXT-WRITEOFF` doubles as the counterparty for count increases: stock that
"appears" during a count came from an untracked source, and posting it against
the write-off account keeps shrinkage arithmetic honest (net shrinkage = the
write-off account's balance).

---

## 4. Posting API

The only way to write to the ledger:

```csharp
public interface IInventoryLedger
{
    Task<Result<PostedMovementGroup>> PostAsync(
        MovementGroupRequest request, CancellationToken ct);
}

public sealed record MovementGroupRequest(
    Guid EventId,
    InventoryMovementType MovementType,
    ReferenceDocumentType ReferenceDocumentType,
    Guid? ReferenceDocumentId,
    string ReferenceNumber,
    IReadOnlyList<MovementLegRequest> Legs,
    LedgerActor Actor,
    DateTimeOffset OccurredAtUtc,
    AdjustmentReasonCode? ReasonCode = null,
    string? Notes = null,
    Guid? ReversesMovementGroupId = null);
```

`PostAsync` performs, inside a single database transaction:

1. **Idempotency check** — if `EventId` already exists, return the stored result
   without writing anything.
2. **Structural validation** — at least two legs; no zero quantities; every leg
   references the same product set as declared; batch presence matches the
   product's `TracksBatches`; costs non-negative.
3. **Zero-sum validation** — `Σ QuantityDelta == 0` **per (ProductId, BatchId)**,
   not merely in total. A group may not net a shortage of rice against a surplus
   of sugar.
4. **State-transition validation** — the movement type must permit the
   (fromState → toState) pair and the (fromLocationKind → toLocationKind) pair.
5. **Authorization** — `Actor` must hold the permission mapped to the movement
   type, must be scoped to the affected locations, and must supply an approver
   when the type or value crosses the configured threshold.
6. **Availability validation** — for every leg with a negative delta on a
   non-external location, the resulting balance must satisfy the negative-stock
   policy (§7).
7. **Append** — insert the movement rows.
8. **Project** — upsert `InventoryBalance` rows with the deltas, under optimistic
   concurrency; on conflict, retry the balance update (never the ledger append).
9. **Audit** — insert an `AuditLog` entry referencing the group.
10. **Domain events** — enqueue `StockLevelChanged`, `LowStockReached`,
    `NegativeStockAttempted`, etc. for the outbox; these are dispatched after commit.

Failure at any step rolls the whole transaction back. There is no partial post.

### 4.2 Cost and valuation

- `UnitCost` on an inbound leg is the **acquisition cost** from the goods receipt.
- `UnitCost` on an outbound leg is the **weighted-average cost** of the bucket at
  the moment of posting, or for batch-tracked products the **batch's** unit cost.
- Weighted average is maintained per `(LocationId, ProductId)` on
  `InventoryBalance.AverageUnitCost`, recomputed on every inbound leg as
  `((qtyOld * avgOld) + (qtyIn * costIn)) / (qtyOld + qtyIn)`, rounded to 4 dp
  with `MidpointRounding.ToEven`.
- Transfers move cost with the goods; the destination's average absorbs the
  source's cost. Transfers therefore never create or destroy inventory value.
- `TotalValueDelta` is always `UnitCost * QuantityDelta`, computed once and
  stored, so historical valuation never shifts when costs change later.

---

## 5. Balances

```csharp
public sealed class InventoryBalance
{
    public Guid LocationId { get; }
    public Guid ProductId  { get; }
    public Guid? BatchId   { get; }
    public InventoryState State { get; }

    public decimal Quantity          { get; private set; }
    public decimal AverageUnitCost   { get; private set; }
    public decimal TotalValue        { get; private set; }
    public Guid    LastMovementId    { get; private set; }
    public DateTimeOffset LastMovementAtUtc { get; private set; }
    public long    Version           { get; private set; }  // optimistic-concurrency token
}
```

Primary key `(LocationId, ProductId, BatchId, State)` with `BatchId` normalised
to `Guid.Empty` when null so the key stays non-nullable and unique.

Derived quantities used across the app:

```
OnHand(location, product)      = Σ Quantity where State in (Available, Reserved, Quarantine,
                                                            PendingInspection, ReturnPending,
                                                            Damaged, Expired)
Sellable(location, product)    = Quantity where State = Available
                                 minus expired batches (see BATCHES)
InTransitOut(location)         = Quantity where State = InTransit
BusinessTotal(product)         = Σ OnHand over all internal locations
```

---

## 6. Worked examples

### 6.1 Supplier receipt with a shortage

PO ordered 100 sacks of rice at 1,200.00 each; 98 arrive, 2 short.

| Leg | Location | State | Δ Qty | Unit cost |
|---|---|---|---|---|
| 1 | `EXT-SUPPLIER` | External | −98 | 1200.0000 |
| 2 | `MAIN` | PendingInspection | +98 | 1200.0000 |

The missing 2 produce **no ledger rows** — stock that never arrived was never
ours. They are recorded as a `ReceivingDiscrepancy` (shortage, 2) against the
goods receipt and reduce the PO's received quantity. Supplier invoicing and the
PO status flow from the discrepancy record, not from the ledger.

Inspection passes:

| Leg | Location | State | Δ Qty |
|---|---|---|---|
| 1 | `MAIN` | PendingInspection | −98 |
| 2 | `MAIN` | Available | +98 |

### 6.2 Main → Store transfer, dispatch

Main has 500 Coke. 100 dispatched to Store 1.

| Leg | Location | State | Δ Qty |
|---|---|---|---|
| 1 | `MAIN` | Available | −100 |
| 2 | `MAIN` | InTransit | +100 |

Result: Main Available 500 → 400, Main InTransit 0 → 100, Store 1 unchanged.
**Nothing lands in Store 1's Available stock at dispatch time.** Custody stays
with the source until the destination confirms receipt — which is why the
in-transit bucket sits at the source location, tagged with
`DestinationLocationId` so it still reports as "in transit to Store 1".

### 6.3 Main → Store transfer, receipt with a discrepancy

Store 1 expected 100, physically counted 98.

| Leg | Location | State | Δ Qty |
|---|---|---|---|
| 1 | `MAIN` | InTransit | −100 |
| 2 | `STORE1` | Available | +98 |
| 3 | `MAIN` | TransitVariance | +2 |

Sum = 0. Store 1 gains exactly what it counted. The 2 missing units remain on
the books at the **source**, in a non-sellable state, valued, and attached to a
`TransferDiscrepancy` record that requires investigation. They leave
`TransitVariance` only through an explicit, permissioned resolution:

- found at Main → `TransitVarianceResolveFound` back to `MAIN/Available`;
- found at Store 1 → `TransitVarianceResolveFound` to `STORE1/Available`;
- written off → `TransitVarianceWriteOff` to `EXT-WRITEOFF` with a reason and an
  approver above the configured value threshold.

Overage is the mirror image: received 102, `TransitVariance` takes −2 (a
negative variance bucket meaning "more arrived than left"), and resolution
either corrects the source's records or posts a count adjustment at the source.

### 6.4 POS sale

Sale of 2 Coke + 1 Rice at Store 1.

| Leg | Location | State | Δ Qty |
|---|---|---|---|
| 1 | `STORE1` | Available | −2 (Coke) |
| 2 | `EXT-CUSTOMER` | External | +2 (Coke) |
| 3 | `STORE1` | Available | −1 (Rice) |
| 4 | `EXT-CUSTOMER` | External | +1 (Rice) |

Zero-sum holds per product. The POS never touches `InventoryBalance`; it calls
`IInventoryLedger.PostAsync` as part of the same transaction that writes the
`Sale`, `SaleItem`s, `Payment`s and `AuditLog`.

### 6.5 Physical count variance

System says 105, physical count says 101.

The count line stores `SystemQuantity = 105`, `PhysicalQuantity = 101`,
`Variance = −4`, status `PendingApproval`. **No ledger rows are written yet and
the balance is not changed.** After approval:

| Leg | Location | State | Δ Qty |
|---|---|---|---|
| 1 | `STORE1` | Available | −4 |
| 2 | `EXT-WRITEOFF` | External | +4 |

Reference `CNT-2026-000001`, reason `CountCorrection`, approver recorded. The
balance becomes 101 *because of the movement*, never by assignment.

---

## 7. Negative stock policy

Configured per location, defaulting to the strictest option:

| Policy | Behaviour |
|---|---|
| `Prohibit` *(default)* | Any post that would drive a bucket below zero is rejected with `InsufficientStock`. |
| `AllowWithPermission` | Permitted only when the actor holds `inventory.negative_stock` and supplies a reason; raises a high-priority exception. |
| `AllowOfflineWithReview` | Offline POS sales only, capped by `MaxNegativeUnitsOffline`; the movement is flagged `RequiresReview` and appears on the exception dashboard. |

Every rejection writes a `NegativeStockAttempt` record and an
`inventory.negative_stock.attempted` audit entry (product, location, bucket,
requested and available quantity, policy, reference document, user, device) so
repeated attempts are visible even though nothing was posted. This is an important
shrinkage signal. The refusal rolls the command back, so the record is written
after the transaction ends, through a separate context (ADR-0030); the table is
append-only. `GET /api/v1/inventory/exceptions/negative-attempts` and its
`/summary` report them.

---

## 8. Immutability enforcement

Four independent layers, because one is not enough:

1. **Domain** — `InventoryMovement` has `init`-only properties and no public
   mutators. It is constructed only by the ledger's internal factory.
2. **EF Core** — the entity is mapped as insert-only; the SaveChanges
   interceptor throws if any `InventoryMovement` or `AuditLog` entry is in
   `Modified` or `Deleted` state.
3. **Database** — `BEFORE UPDATE OR DELETE` triggers on `inventory_movement` and
   `audit_log` raise an exception. The application's database role is granted
   only `SELECT, INSERT` on these tables.
4. **Balance guard** — a trigger on `inventory_balance` verifies that the change
   in `quantity` for a row equals the sum of movements inserted in the same
   transaction for that bucket; a mismatch aborts the transaction. This is what
   makes `product.StockQuantity = 100` impossible even by accident.

Additionally, `Pos.Architecture.Tests` fails the build if any type outside
`Pos.Infrastructure.Inventory` references `InventoryBalance` in a writable
context, or if any project other than the ledger constructs `InventoryMovement`.

---

## 9. Reconciliation and rebuild

- **Continuous check** (each post): the balance guard trigger above.
- **Nightly job** `LedgerReconciliationWorker`:
  ```sql
  SELECT location_id, product_id, batch_id, state, SUM(quantity_delta) AS ledger_qty
  FROM inventory_movement GROUP BY 1,2,3,4
  ```
  compared against `inventory_balance`. Any drift raises a `Critical` notification
  to the owner, writes an `IntegrityIncident`, and is never auto-corrected.
- **Rebuild command** `pos-admin rebuild-balances --as-of <utc>` recreates the
  entire projection from the ledger. It is safe to run at any time, takes a
  table lock on `inventory_balance` only, and is the disaster-recovery path.
- **Global assertion** `SELECT SUM(quantity_delta) FROM inventory_movement = 0`
  runs on every reconciliation pass.

---

## 10. Traceability

Because every leg carries `ReferenceDocumentType`/`ReferenceDocumentId` and
groups are linked by `MovementGroupId`, the system can walk a chain:

```
GRN-2026-000014  Supplier "Rice Traders Inc."  ->  MAIN        (+100)
TRF-2026-000031  MAIN -> STORE2                                 (-40 / +40)
TRF-2026-000055  STORE2 -> STORE1                               (-15 / +15)
SAL-2026-D03-000812  STORE1 -> Customer                         (-2)
```

The owner UI renders this as a movement chain per product and per batch; batch
tracking makes the chain exact rather than inferred, which is why perishable
goods are batch-tracked by default.
