# Afno Parcel — Status tracking (Phase 4)

How a parcel moves through its journey, who may move it, what is recorded each time, and what customers can see.
All timestamps are UTC (display them in Nepal time, UTC+05:45).

## The ten statuses

| Id | Code | Meaning |
|----|------|---------|
| 1 | `BookingCreated` | Booking confirmed; waiting to be handed over |
| 2 | `PickedUp` | Collected from the sender, or received at the counter |
| 3 | `Shipped` | Dispatched from the origin branch into the network |
| 4 | `InTransit` | On its way between branches or hubs |
| 5 | `ArrivedAtWarehouse` | Received and scanned at a warehouse or hub |
| 6 | `OutForDelivery` | With a rider for the final leg |
| 7 | `Delivered` | Handed to the receiver (final) |
| 8 | `DeliveryFailed` | A delivery attempt did not work |
| 10 | `Returned` | Returned to the sender (final) |
| 11 | `Cancelled` | Booking cancelled (final) |

Ids never change and are never reused (9 belonged to a status removed in this phase). In the Phase 3 → 4 migration the old
`Booked`, `ReceivedAtBranch` and `ReceivedAtWarehouse` rows became `BookingCreated`, `Shipped` and `ArrivedAtWarehouse`, and
`ReturnToSender` was removed. Phase 3 could not move a parcel beyond `Booked`, so no real history was affected.

## The workflow

A parcel can only follow these arrows. Everything else is refused with `422` and an explanation of what is allowed.

```
BookingCreated ──► PickedUp ──► Shipped ──► InTransit ◄──► ArrivedAtWarehouse
      │               │            │            │  ╲            │
      ▼               ▼            │            │   ╲           │
  Cancelled       Cancelled        └────────────┴────┴──► OutForDelivery ──► Delivered
                                                              │   ▲
                                                              ▼   │ retry
                                                         DeliveryFailed ──► (back via InTransit / ArrivedAtWarehouse)
                                                              │
                                                              ▼
                                                          Returned
```

| From | May move to |
|------|-------------|
| `BookingCreated` | `PickedUp`, `Cancelled` |
| `PickedUp` | `Shipped`, `Cancelled` |
| `Shipped` | `InTransit`, `ArrivedAtWarehouse`, `OutForDelivery` |
| `InTransit` | `ArrivedAtWarehouse`, `OutForDelivery`, `Returned`* |
| `ArrivedAtWarehouse` | `InTransit`, `OutForDelivery`, `Returned`* |
| `OutForDelivery` | `Delivered`, `DeliveryFailed` |
| `DeliveryFailed` | `OutForDelivery` (retry), `ArrivedAtWarehouse`, `InTransit`, `Returned` |
| `Delivered`, `Returned`, `Cancelled` | nothing: they are final |

\* `Returned` is only possible after a delivery attempt has failed. The statuses a parcel may take next are also listed on
the parcel itself (`allowedNextStatuses`, already filtered to what the signed-in person's role may do).

Other rules enforced by the domain, on every change:

- **Remarks are required** for `DeliveryFailed`, `Returned` and `Cancelled` (max 500 characters).
- **Where it happened** must be recorded: `Shipped` needs a branch, `ArrivedAtWarehouse` needs a warehouse,
  `OutForDelivery` and `Returned` need a branch or a warehouse (never both). The branch or warehouse must exist and be active.
- A change cannot be dated earlier than the one before it.
- Cancelling is not part of the status API: it has its own endpoint and rules (see [booking.md](booking.md)).

## Who may do what

| Status | Roles |
|--------|-------|
| `PickedUp` | SuperAdmin, BranchManager, BookingStaff, DeliveryAgent |
| `Shipped` | SuperAdmin, BranchManager, BookingStaff, WarehouseStaff |
| `InTransit`, `ArrivedAtWarehouse` | SuperAdmin, BranchManager, WarehouseStaff |
| `OutForDelivery` | SuperAdmin, BranchManager, WarehouseStaff, DeliveryAgent |
| `Delivered`, `DeliveryFailed` | SuperAdmin, BranchManager, DeliveryAgent |
| `Returned` | SuperAdmin, BranchManager, WarehouseStaff |

Delivery agents appear nowhere in this table since Phase 6: they can change a parcel's status only on parcels assigned to them,
through the agent API, which also collects the proof of delivery and the cash (see [delivery.md](delivery.md)). Customers and
accountants can never change a status. A role that may not make a change gets `403` before the parcel is even
looked up, so it learns nothing about it. The same table is used by the scan API.

## The history is append-only

Every change adds one row to `ParcelStatusHistory`:

| Column | Meaning |
|--------|---------|
| `FromStatusId`, `ToStatusId` | the previous and the new status |
| `ChangedByUserId`, `ChangedByName` | the staff user (id, plus their name as it was at the time) |
| `BranchId` or `WarehouseId` | where it was recorded, when relevant |
| `OccurredAtUtc` | when |
| `Remarks` | why or what, in the staff member's words |

Rows are never updated or deleted. The aggregate only ever adds rows, and a save guard (`ParcelHistoryGuardInterceptor`)
refuses any save that would modify or delete an existing history row, so even a future bug cannot rewrite history. Foreign
keys are restrictive, so a parcel with history cannot be deleted either. Each change is also written to `AuditLogs` (no
remarks, no personal data).

Staff see who made each change; customers see the same journey without staff names.

## Staff APIs

| Method | Route | Notes |
|--------|-------|-------|
| POST | `/api/v1/parcels/{id}/status` | `{ "status": "Shipped", "remarks": "...", "branchId": "..." }` |
| POST | `/api/v1/parcels/tracking/{trackingNumber}/status` | the same, for staff who only have a label |
| POST | `/api/v1/scans`, `/api/v1/scans/batch` | warehouse scans (Phase 5, see [network.md](network.md)) |
| GET / POST | `/api/v1/warehouses` | list (staff) / register (SuperAdmin, BranchManager of the branch) |

**Scans** were introduced here as a foundation and completed in Phase 5: a scan is a normal status change (same workflow,
role and location rules), and it also records a warehouse movement and a scan-log row. Scanning the same thing twice is now
answered `200` with `"outcome": "Duplicate"` and changes nothing, staff must be assigned to the warehouse's branch, and
barcodes and QR links are both accepted. See [network.md](network.md) for the whole story.

**Staff at a warehouse**: a change that names a warehouse (`ArrivedAtWarehouse`, a dispatch, `OutForDelivery` from a hub)
also records the movement, and only `SuperAdmin` or staff assigned to that warehouse's branch may make it.

## Customer tracking

| Method | Route | Who |
|--------|-------|-----|
| GET | `/api/v1/tracking/{trackingNumber}` | anyone |
| POST | `/api/v1/tracking/{trackingNumber}/otp` | anyone (sends a code) |
| POST | `/api/v1/tracking/{trackingNumber}/verify` | anyone with the code |
| GET | `/api/v1/tracking/{trackingNumber}/details` | verified receiver, or signed-in sender/staff |

### Public tracking: no personal data

Anyone with a tracking number sees: the status and a standard sentence for it, the service type, the **districts** the
parcel travels between, the estimated and actual delivery time, and the complete timeline (status, standard sentence, time
and the **name of the branch or warehouse**). It never contains names, phone numbers, street, ward or municipality,
amounts, payment details, staff names, or the free-text remarks staff type. This is checked by tests that book a parcel
with recognisable private details and assert none of them appear. Unknown and malformed numbers get the same `404`, and
lookups are rate limited per IP (`RateLimiting:TrackingPermitPerMinute`).

### Detailed tracking: proof required

Detailed tracking adds the receiver's name, their area (municipality-ward and district, not the street), the amount to pay
on delivery and the payment method. It still never includes phone numbers, the sender, the street or landmark, or staff
identities. It needs one of:

1. **Receiver verification with a one-time code (OTP)**
   1. `POST …/otp` sends a 6-digit code by SMS to the receiver's phone number on the parcel. The answer is always `202` with
      the same message, whether or not the number exists, and never mentions the phone number.
   2. `POST …/verify` with `{ "code": "123456" }` returns an access token valid 15 minutes for that one tracking number.
   3. `GET …/details` with the token in the `X-Tracking-Access` header returns the details. A receiver verified this way does
      **not** see staff remarks.
2. **Signing in**: the sender (their own parcels only) and staff see the details with the staff remarks included. Anyone
   else gets `404`.

Without a token or a sign-in the answer is `401`.

How the code is protected:

- Generated with a cryptographic random generator; only a **keyed hash** (HMAC-SHA256, bound to the parcel, key derived from
  the server secret) is stored, never the code.
- Valid for 10 minutes, usable **once**, and locked after **5** wrong guesses. Only the newest code works.
- At most 3 codes per parcel per hour; extra requests are ignored silently (so the endpoint reveals nothing and cannot spam a
  receiver). Requests and verifications are also rate limited per IP.
- Every failure (unknown number, no code requested, wrong, expired, locked, already used) returns the same `400` message.
- The access token is signed with a different derived key, bound to one tracking number, and expires; comparisons are
  constant-time.

**SMS delivery**: the code is written to the notification outbox (`NotificationOutbox`, template `tracking-otp`, channel
SMS) in the same transaction as the code itself. The background worker sends it through the configured SMS provider (see
[notifications.md](notifications.md)); with the default **mock** provider no real SMS leaves the system and the text can be read in
the test inbox. Once sent (or given up on) the code is erased from the outbox row. For local development set `Tracking:LogCodes` to `true`
(already the case in `appsettings.Development.json`): the API log then shows a `DEVELOPMENT ONLY` line with the code. **Never
enable it in production**, because anyone who can read the logs could view private tracking details.

## Web app

- `/track` and `/track/:number`: the public timeline with a progress tracker, then "See full details" (sign in, or verify as
  receiver with a code).
- Parcel details (staff): an "Update status" panel offering only the statuses the caller may set, asking for remarks, branch
  or warehouse where required, and a timeline showing from → to, place, time, remarks and who.
- `/scan` (SuperAdmin, BranchManager, WarehouseStaff): the warehouse scan screen, rebuilt in Phase 5 (see [network.md](network.md)).

## Tests

- Unit: the full workflow matrix (every from/to pair), role permissions, remarks and location rules, the "returned only
  after a failure" rule, back-dating, the status, scan, tracking and OTP services (privacy of the public view, generic
  answers, attempt limits, expiry, single use, tokens bound to a parcel) and all validators.
- Integration (real HTTP API, in-memory SQLite): the whole journey by the right roles, invalid transitions, forbidden roles,
  required remarks and locations, history rows with every field, the append-only guard, the audit trail, public tracking with
  a privacy check on the raw JSON, the OTP flow end to end (including lock-out, single use, throttling and forged tokens),
  detailed tracking for senders and staff, warehouses and single and batch scans.
