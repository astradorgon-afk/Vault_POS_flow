# Quarantine and Unauthorized Inventory

The software cannot stop someone physically carrying a box into a store. It can
guarantee that the box never becomes sellable inventory without an explicit,
recorded, authorized decision by the Main Warehouse. That guarantee is this
module's entire purpose.

> **Status (Phase 8 delivered, 2026-09-13):** the incident aggregate, lines and
> photos, the quarantine ledger postings, and every HQ resolution below
> (register, link, release with caps, reject, write-off) are implemented and
> covered by endpoint tests. The sections that are **not yet built** are the
> *automatic* triggers in §1 (incidents are raised through `POST
> /api/v1/quarantine`; scan/receiving/POS detection is future integration), the
> direct-delivery machinery in §3 (receiving POSTs to quarantine only when an
> incident is raised), and all of §7. See [API.md §7](API.md) for the live route
> table and [STATUS.md](STATUS.md) for where the build stands.

---

## 1. Triggers

A quarantine incident is raised automatically in every one of these situations:

| Trigger | Detected at | Incident kind |
|---|---|---|
| Scanned barcode is not in the Product Master | POS, receiving, stock terminal | `UnknownBarcode` |
| Supplier delivers to a store with no approved PO and no direct-delivery authorization | Store receiving | `UnauthorizedSupplierDelivery` |
| Received quantity exceeds the PO's over-receipt tolerance | Warehouse or store receiving | `OverReceipt` |
| Goods fail inspection after receipt | Warehouse | `FailedInspection` |
| Customer return whose condition is unclear | POS | `ReturnedGoods` |
| Supplier or regulator recall | HQ | `Recall` |
| Transfer arrives containing an item not on the transfer order | Destination store | `WrongItem` |

In all cases the physical quantity is posted to the `Quarantine` state at the
reporting location. It is inventory — counted, valued, auditable — but it is not
sellable, not transferable, and not reportable as available stock.

---

## 2. The unknown-barcode path

This is the flow the brief calls out specifically.

```
Employee scans 4801234567890 at Store 2
        |
        v
GET /products/by-barcode/4801234567890  ->  404 catalog.barcode_unknown
        |
        v
Client does NOT offer "create product". It offers "Report unregistered item".
        |
        v
POST /quarantine/incidents
  { kind: UnknownBarcode, locationId, rawBarcode, quantity,
    claimedSupplierId | claimedSupplierName, notes, photos[] }
        |
        +--> LEDGER: EXT-SUPPLIER/External -q  ->  STORE2/Quarantine +q
        +--> AUDIT:  quarantine.incident.created
        +--> NOTIFY: Main Warehouse + Owner (Warning)
        |
        v
Incident QRT-2026-000014 status = Open
```

Captured on the incident: location, raw barcode, quantity, reporting employee,
timestamp (device + server), device id, claimed supplier or source, free-text
notes, and optional photos.

At the POS the same 404 blocks the sale: an unknown barcode can never be rung up,
with or without a price. There is no "sell at a manual price" path for an
unregistered item.

---

## 3. Unauthorized supplier delivery to a store

Normal flow is supplier → Main Warehouse. Direct-to-store delivery is a
controlled exception requiring **either**:

- an approved purchase order whose `destination_location_id` is that store, **or**
- an explicit `DirectDeliveryAuthorization` issued by a holder of
  `purchase.direct_to_store.authorize` (scoped to supplier, store, date window,
  and optionally a product/value cap).

When a store attempts to receive with neither:

1. Receiving is **not** blocked — the goods are physically there and pretending
   otherwise creates untracked stock.
2. Everything received posts to `Quarantine`, never to `Available`.
3. An `UnauthorizedSupplierDelivery` incident is created with the delivery
   document reference, claimed supplier, driver name if given, and photos.
4. Main Warehouse and the Owner are notified immediately (Critical).
5. The store cannot release it. Only HQ can.

---

## 4. Incident states

```
Open
 |  HQ picks it up
 v
UnderReview ----------------------------------+
 |            |            |                  |
 | approve    | partial    | reject           | needs facts
 v            v            v                  v
Approved  PartiallyApproved  Rejected    UnderInvestigation
 |            |               |                  |
 | release    | release part  | return/write-off | findings
 v            v               v                  v
      (ledger posts)     ReturnedToSupplier   back to UnderReview
 |            |               |
 +------------+---------------+
              |
              v
           Closed  (only when quarantine quantity for the incident reaches zero)
```

---

## 5. HQ resolution options

All require `quarantine.release` or `quarantine.reject`, held only by
Owner / Administrator / Main Inventory Manager.

| Action | Effect |
|---|---|
| **Register the product** | Creates the product and its barcode in the Product Master (`product.create`), then releases against it |
| **Link to an existing product** | Attaches the barcode to an existing product (`product.barcode.manage`), then releases |
| **Approve the delivery** | Releases the approved quantity: `Quarantine −q → Available +q`, with unit cost set from the supporting document |
| **Approve partially** | Releases part; the remainder stays quarantined with a note |
| **Reject** | `Quarantine −q → EXT-SUPPLIER +q` (`QuarantineReject`), supplier return document created |
| **Require investigation** | Moves to `UnderInvestigation`; stock stays quarantined indefinitely |
| **Return to supplier** | As Reject, plus a `SupplierReturn` with a return authorization reference |
| **Mark unauthorized / write off** | `Quarantine −q → EXT-WRITEOFF +q` with reason and an approver above the value tier |

Release is capped at the approved quantity per line. Over-release is rejected at
the ledger. An incident cannot be `Closed` while any quantity remains in
`Quarantine`, which is enforced by a domain invariant and verified by a test.

---

## 6. Invariants

1. Quarantined stock is **never** sellable: `Sellable()` reads only `Available`.
2. Quarantined stock is **never** transferable: transfer picking sources only
   `Available`.
3. Only HQ permissions can move quantity out of `Quarantine`.
4. Every transition writes an audit entry with before/after status and reason.
5. Registering a product from an incident follows the normal Product Master
   rules — the store still cannot create products; HQ does it as part of resolution.
6. The barcode on an incident is stored raw and is never auto-attached to a
   product by similarity, fuzzy match, or "nearest SKU" heuristic.

---

## 7. Notifications and visibility

- Incident raised → `Warning` (unknown barcode) or `Critical` (unauthorized
  delivery) notification to `quarantine.release` holders and the Owner.
- Age-based escalation: open beyond 24 h → reminder; beyond 72 h → Critical.
- The Owner dashboard's exception panel shows **Unknown products**, **Unexpected
  receiving**, and **Quarantine inventory (value)** with drill-down to the
  incident, the photos, the ledger group, and the audit trail.
- The quarantine report lists quantity and value by location, age bucket, and
  incident kind, which is the primary control report for this module.
