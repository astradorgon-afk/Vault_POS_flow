# Purchasing and Supplier Receiving

---

## 1. Normal flow

```
Purchase Order (Draft)
   -> Pending Approval  -> Approved  -> Ordered
   -> supplier delivers
   -> Main Warehouse Receiving  -> Verification
   -> Goods Receipt (posts the ledger)
   -> Partially Received / Fully Received -> Closed
```

## 2. Purchase order states

| State | Enter by | Exit to |
|---|---|---|
| `Draft` | `purchase.create` | `PendingApproval`, `Cancelled` |
| `PendingApproval` | submit | `Approved`, `Rejected`, `Cancelled` |
| `Approved` | `purchase.approve` + tier | `Ordered`, `Cancelled` |
| `Rejected` | `purchase.approve` | terminal (re-draft copies to a new PO) |
| `Ordered` | mark as sent to supplier | `PartiallyReceived`, `FullyReceived`, `Cancelled` |
| `PartiallyReceived` | first goods receipt | `PartiallyReceived`, `FullyReceived`, `Closed` |
| `FullyReceived` | receipts cover ordered quantity | `Closed` |
| `Closed` | `purchase.approve` (reason required when not fully received) | terminal |
| `Cancelled` | `purchase.approve` | terminal |

Approval rules: the value tier of the approver must cover the PO's grand total
(PERMISSIONS.md §4); self-approval above `SelfApprovalLimit` is refused; every
decision writes a `purchase_approval` row and an audit entry.

---

## 3. Receiving

**Ordered quantity is never assumed to equal received quantity.** A goods receipt
line records three independent numbers:

```
QuantityExpected   from the PO line (remaining)
QuantityReceived   physically counted by the receiver
QuantityRejected   counted but refused (damaged, expired, wrong item)
```

Worked example from the brief:

| Item | Ordered | Received | Rejected | Discrepancy |
|---|---|---|---|---|
| Rice 25kg | 100 | 98 | 0 | shortage 2 |

Ledger effect: `EXT-SUPPLIER −98 → MAIN/PendingInspection +98`. The two missing
sacks produce **no ledger rows** — goods that never arrived were never ours.
They are recorded as a `ReceivingDiscrepancy(Shortage, 2)` with a value impact,
they reduce the PO line's received quantity, and they drive the supplier
performance report and the invoice reconciliation.

| Discrepancy kind | Ledger effect | Follow-up |
|---|---|---|
| `Shortage` | none | credit expected from supplier |
| `Overage` within tolerance | received into `PendingInspection` | no discrepancy row; accumulates into the PO line's received total |
| `Overage` beyond tolerance | excess into `Quarantine` | `Overage` discrepancy row; quarantine incident / HQ decision is a follow-up |
| `Damaged` | into `Damaged` state | supplier return or write-off |
| `WrongItem` | into `Quarantine` | quarantine incident |
| `Expired` / short-dated | into `Quarantine` | reject or accept with price concession |
| `MissingDocuments` | received into `PendingInspection` | blocks invoice matching |

Inspection then moves `PendingInspection` to `Available` (pass) or `Damaged`
(fail). A location may set `AutoPassInspection = true` for trusted, non-perishable
categories, in which case receipts post straight to `Available`; the setting is
per location and per category and is audited when changed.

---

## 4. Batches and expiry

For products with `TracksBatches`, each goods receipt line **must** supply a lot
number, and `ExpiresOn` is mandatory when `TracksExpiry` is set. The receipt
creates (or reuses) a `Batch` carrying supplier, received date, manufacture date,
expiry, and unit cost. That batch id then travels with every subsequent movement
of those goods, which is what makes recall and FEFO exact rather than estimated.

Receiving validation refuses an expiry date already in the past unless the
*entire* lot is rejected on arrival — a partially accepted expired lot is
`receipt_expired_on_arrival`, because the past-dated units would otherwise leak
into stock. A short-dated cutoff (warn below the product's minimum acceptable
shelf life) is a planned follow-up.

---

## 5. Costing

- Unit cost on the receipt is the **actual** purchase cost for that delivery,
  including allocated landed costs where entered.
- It updates `ProductSupplier.LastCost` and feeds the weighted average on
  `InventoryBalance` (INVENTORY_LEDGER.md §4.2).
- A receipt whose unit cost deviates from the PO's by more than
  `CostVarianceTolerancePercent` (default 5%) refuses to post unless the receiver
  holds `purchase.approve` (the usual value-tier check applies), and the
  approving user is then recorded on each deviating line. The order's creator
  cannot fill that role: the self-approval rule extends to the cost-variance
  grant. The receipt is flagged `costVariancePendingApproval` so the variance is
  surfaced rather than absorbed; a notification/queue item is a known follow-up.
- `Product.DefaultPurchaseCost` is **not** updated automatically by receiving; it
  is a planning figure changed deliberately under `product.edit`.

---

## 6. Direct supplier-to-store delivery

Supported, but as a controlled exception. See [QUARANTINE.md](QUARANTINE.md) §3.

Authorized path: an approved PO with the store as destination, **or** a
`DirectDeliveryAuthorization` (supplier + store + date window + optional product
and value cap) issued under `purchase.direct_to_store.authorize`. Receiving then
behaves exactly like warehouse receiving, at the store.

Unauthorized path: goods are received into `Quarantine`, an incident is raised,
HQ is notified, and only HQ can release them.

---

## 7. Supplier returns

```
SupplierReturn (Draft -> PendingApproval -> Approved -> Dispatched -> Confirmed)
```

Sources stock from `Damaged`, `Expired` or `Quarantine` states — never from
`Available` without an explicit adjustment first, so a return cannot be used to
quietly remove sellable stock. Ledger effect on dispatch:
`Loc/<state> −q → EXT-SUPPLIER +q`, reference `SRT-…`, with the supplier's return
authorization number recorded.

---

## 8. Supplier performance

Computed from receipts and discrepancies, per supplier per period:

- on-time delivery rate (promised vs actual receipt date)
- fill rate (received ÷ ordered)
- shortage rate, damage rate, wrong-item rate
- average cost variance vs PO
- average lead time vs the supplier's stated lead time
- open discrepancy value

These feed `GET /api/v1/reports/supplier-performance` and the supplier's rating.

---

## 9. Tests

| Test | Asserts |
|---|---|
| `Receipt_PostsLedgerForReceivedQuantityOnly` | 98 received ⇒ 98 posted |
| `Shortage_CreatesDiscrepancy_NoLedgerRows` | 2 short ⇒ discrepancy only |
| `OverageBeyondTolerance_GoesToQuarantine` | excess quarantined + incident |
| `PartialReceipts_AccumulateToFullyReceived` | 60 + 40 ⇒ FullyReceived |
| `BatchTrackedProduct_RequiresLotAndExpiry` | validation |
| `ExpiredOnArrival_CannotPostToAvailable` | quarantine path |
| `SelfApprovalAboveLimit_Rejected` | approval rules |
| `CostVarianceAboveTolerance_RequiresApproval` | costing control |
| `UnauthorizedDirectDelivery_Quarantines_AndNotifies` | exception path |
| `SupplierReturn_CannotSourceFromAvailable` | control |
