# Afno Parcel — Branches, warehouses and scanning (Phase 5)

How the delivery network is managed (branches, warehouses, who works where), how parcels are received and sent on at a
warehouse, and how every movement is recorded. All timestamps are UTC (display them in Nepal time, UTC+05:45).

## Who manages what

| Task | Who |
|------|-----|
| Create a branch or head office | `SuperAdmin`, `BranchManager` |
| Edit, switch off or switch on a branch | `SuperAdmin`, or the `BranchManager` assigned to that branch |
| Create / edit / switch off / switch on a warehouse | `SuperAdmin`, or the `BranchManager` of the operating branch |
| Assign staff to a branch or warehouse | `SuperAdmin` (anyone, anywhere); `BranchManager` (into their own branch only) |
| Scan parcels at a warehouse | `SuperAdmin`, `BranchManager`, `WarehouseStaff` who work there |
| See warehouse inventory, waiting lists, scan log, movement history | `SuperAdmin`, `BranchManager`, `BookingStaff`, `WarehouseStaff`, `Accountant` |

A branch manager has no powers over other branches. Everyone else's reach follows their **assignment**.

### Staff assignments

A staff member works at **one branch**, and optionally at **one warehouse** of it. Changing the assignment ends the old one
and starts a new one, so the history of who worked where is kept (`StaffAssignments`; a database rule allows only one active
row per person).

- Assigned to a branch only → may work at **every** warehouse of that branch.
- Assigned to a warehouse → may work at **that warehouse only**.
- `SuperAdmin` needs no assignment and may work anywhere.
- Nobody else may scan, or change a status *at a warehouse*, without an assignment that covers it. They get `403` with a
  message saying so. Changes made at a branch (for example `Shipped` at the counter) still follow the role rules only.

Only staff accounts can be assigned (not customers), accounts must be active, the branch and warehouse must be active, and
the warehouse must belong to the chosen branch. A branch manager cannot take someone from another branch, nor change a
`SuperAdmin`'s assignment.

A branch **cannot be switched off** while it still has active warehouses, open parcels (booked, picked up or shipped there)
or assigned staff. A warehouse **cannot be switched off** while it holds parcels or parcels are on their way to it. Both
answers are `422` with the numbers. Inactive branches and warehouses are hidden from everyone except managers, who can ask for
them with `includeInactive=true`. Nothing is ever deleted.

## Scanning

A scan is what a hub worker does with a barcode or QR code: find the parcel, say what happened, record it.

`POST /api/v1/scans`

```json
{ "trackingNumber": "AFNK7M2Q9XT4B3", "scanType": "Arrival", "warehouseId": "…", "remarks": "Dock 3", "toWarehouseId": null }
```

| `scanType` | Meaning | Parcel becomes |
|------------|---------|----------------|
| `Arrival` | The parcel arrived at this warehouse | `ArrivedAtWarehouse` |
| `Dispatch` | The parcel left this warehouse (optionally for `toWarehouseId`) | `InTransit` |
| `OutForDelivery` | Handed to a rider for the last leg | `OutForDelivery` |

`trackingNumber` is whatever the scanner read: the number itself (any case, with or without spaces or dashes) **or the whole
tracking link a QR code carries** (`https://…/track/AFNK7M2Q9XT4B3`). Staff can also type or paste it.

A scan is a normal status change: the workflow, the role table in [tracking.md](tracking.md) and the location rules all apply,
the status history gets its row, the audit log gets its entry. On top of that it writes the warehouse ledgers below.

### Every scan records

| Field | Where |
|-------|-------|
| Parcel ID | `ParcelScans.ParcelId`, `ParcelMovements.ParcelId` |
| Warehouse ID | `…WarehouseId` |
| Staff ID (and their name at the time) | `…StaffUserId`, `…StaffName` |
| Scan type | `…ScanType` |
| Timestamp | `ScannedAtUtc` / `OccurredAtUtc` (set by the server clock, never by the client) |
| Remarks | `…Remarks` (max 500) |
| Outcome | `ParcelScans.Outcome`: `Applied` or `Duplicate` |

- **`ParcelMovements`** is the parcel's movement history: one row per real arrival, departure or hand-over, oldest first
  (`GET /api/v1/parcels/{id}/movements`). A dispatch may carry the warehouse it is heading to.
- **`ParcelScans`** is the scan log: one row for *every* scan that reached a known parcel at a real warehouse, including
  repeats (`GET /api/v1/scans`, filter by warehouse, tracking number, type, outcome, staff and Nepal calendar dates).
- Both are **append-only**. A save guard (`ParcelHistoryGuardInterceptor`, the same one that protects the status history)
  refuses any save that would edit or delete a row, and every foreign key is restrictive.

### Duplicate scans never create duplicate movements

Scanners beep twice, people double-tap, batches get sent again. A scan is a **duplicate** when the parcel's most recent
movement is the same scan type at the same warehouse **and** the parcel is already in the status that scan produces. A
duplicate is answered `200` with `"outcome": "Duplicate"` and a message ("Already recorded: Arrival at … on …. Nothing was
changed."). It writes one `ParcelScans` row (outcome `Duplicate`, pointing at the movement it repeated) and **no** movement,
no status history row and no status change. Anything else goes through the normal workflow: for example a parcel that is
`ArrivedAtWarehouse` and was never dispatched cannot be "arrived" somewhere else without first leaving.

Scan results:

| `outcome` | Meaning |
|-----------|---------|
| `Applied` | A new movement was recorded |
| `Duplicate` | Already recorded; nothing changed (still counts as success for the scanner) |
| `Failed` | Refused (batch only; a single scan answers with a problem response, e.g. `422`) |

### Moving parcels between warehouses

Dispatch with `toWarehouseId` to say where the parcel is going. While it is `InTransit` the parcel remembers that destination
(`NextWarehouseId`): the receiving warehouse sees it under **inbound**, and the destination is cleared as soon as the parcel
arrives anywhere. An arrival at a *different* warehouse than the one it was sent to is **recorded, not blocked**, and flagged
`isUnexpectedArrival` so staff can investigate. A destination can only be named on a dispatch from a warehouse, must be a
different, active warehouse, and is optional (leave it out when the destination is not known).

### Batches

`POST /api/v1/scans/batch` takes `{ "scanType", "warehouseId", "trackingNumbers": [...], "remarks", "toWarehouseId" }`
(1–100). The warehouse, scan type and the caller's access are checked once. Each parcel then succeeds, is a duplicate, or
fails **on its own**: the answer lists every entry with its outcome or error and totals (`succeeded`, `duplicates`,
`failed`). The same parcel listed twice (or once as a number and once as a link) is scanned once. A failed entry changes
nothing.

## Inventory and waiting lists

- `GET /api/v1/warehouses/{id}/inventory?search=&page=&pageSize=`: what the warehouse **holds** (status
  `ArrivedAtWarehouse` and located there), longest-waiting first, with how long each has been there; what is **inbound**
  (in transit with this warehouse as its destination); counts; and `utilisationPercent` when the warehouse has a capacity.
  `search` matches any part of the tracking number, or the receiver's district.
- `GET /api/v1/warehouses` shows `parcelsHeld` and `parcelsInbound` for every warehouse.
- `GET /api/v1/inventory/waiting?kind=&branchId=&warehouseId=`: parcels waiting for somebody to act, longest first.
  `kind=Pickup`: booked and waiting to be collected or handed over (`BookingCreated`). `kind=Dispatch`: picked up and waiting
  at a branch to be sent on (`PickedUp`), or sitting at a warehouse (`ArrivedAtWarehouse`). Leave `kind` out for both.
  `waitingHours` is measured from the last status change (`Parcels.StatusChangedAtUtc`).

## Labels, barcodes and QR codes

`GET /api/v1/parcels/{id}/label` (the sender, and staff) returns:

| Field | |
|-------|-|
| `barcodeDataUri` | Code 128 barcode of the tracking number, an SVG as a `data:` address |
| `qrDataUri` | QR code (error correction M) of `qrContent` |
| `qrContent` | `{Tracking:PublicBaseUrl}/track/{trackingNumber}`: scanning it with any phone opens public tracking; the warehouse scanner accepts it too |
| `trackingNumber`, `serviceType`, `quantity`, `weightKg`, `originBranch`, `destinationArea`, `receiverName`, `receiverArea`, `codAmount`, `bookedAtUtc` | what the person handling the parcel needs |

The label never contains phone numbers or the street address. Images are generated on the server with ZXing.Net (pure code,
no fonts or graphics libraries) as plain SVG: no scripts, no external references. The web app shows them in an `<img>` tag
only, and a test draws the SVGs into pixels and decodes them back to prove they scan. Set `Tracking:PublicBaseUrl` to the
public address of the customer website (default `http://localhost:5173`).

`GET /api/v1/parcels/lookup?code=` finds a parcel (staff) from scanner text, and `GET /api/v1/parcels/{id}/movements` is the
movement history.

## API summary

| Method | Route | Who |
|--------|-------|-----|
| GET | `/api/v1/branches?search=&type=&includeInactive=` | anyone (inactive: managers) |
| POST | `/api/v1/branches` | SuperAdmin, BranchManager |
| PUT | `/api/v1/branches/{id}` | SuperAdmin, BranchManager of that branch |
| POST | `/api/v1/branches/{id}/activate`, `/deactivate` | SuperAdmin, BranchManager of that branch |
| GET | `/api/v1/warehouses`, `/api/v1/warehouses/{id}` | staff |
| POST | `/api/v1/warehouses` | SuperAdmin, BranchManager of the branch |
| PUT | `/api/v1/warehouses/{id}` | same |
| POST | `/api/v1/warehouses/{id}/activate`, `/deactivate` | same |
| GET | `/api/v1/warehouses/{id}/inventory` | SuperAdmin, BranchManager, BookingStaff, WarehouseStaff, Accountant |
| GET | `/api/v1/inventory/waiting` | same |
| GET | `/api/v1/staff?search=&role=&branchId=&warehouseId=&unassigned=` | SuperAdmin, BranchManager |
| PUT / DELETE | `/api/v1/staff/{userId}/assignment` | SuperAdmin; BranchManager (own branch) |
| GET | `/api/v1/staff/me/assignment` | any staff member |
| POST | `/api/v1/scans`, `/api/v1/scans/batch` | SuperAdmin, BranchManager, WarehouseStaff (assigned) |
| GET | `/api/v1/scans` | SuperAdmin, BranchManager, BookingStaff, WarehouseStaff, Accountant |
| GET | `/api/v1/parcels/{id}/movements` | same |
| GET | `/api/v1/parcels/lookup?code=` | same |
| GET | `/api/v1/parcels/{id}/label` | the sender, and staff |

Errors are the usual problem responses: `400` validation, `401`, `403` wrong role or not assigned there, `404`, `409` already
in that state, `422` a business rule refused it (workflow, still has parcels, …).

## Data model

New tables (schema `parcel`): `StaffAssignments`, `ParcelMovements`, `ParcelScans`. `Parcels` gains `StatusChangedAtUtc` (set
on booking and on every status change; existing rows are back-filled from their latest history row by the migration) and
`NextWarehouseId`. See [database.md](database.md).

## Web app (staff)

| Page | Route | Who |
|------|-------|-----|
| Scan | `/scan` | SuperAdmin, BranchManager, WarehouseStaff. Starts on your own warehouse; Arrival / Dispatch (with destination) / Out for delivery; live mode (one code at a time, green/amber/red result list) or batch mode; accepts barcode scanner input, pasted QR text and, where the browser supports `BarcodeDetector` on HTTPS or localhost, the camera |
| Branches | `/admin/branches` | create, edit, switch on/off, counts |
| Warehouses | `/admin/warehouses` | create, edit, switch on/off, parcels held/inbound |
| Warehouse inventory | `/admin/warehouses/:id/inventory` | held and inbound lists, search, utilisation |
| Waiting parcels | `/admin/inventory` | pickup and dispatch lists |
| Staff | `/admin/staff` | who works where; assign, move, unassign |
| Scan log | `/admin/scan-log` | every scan, with filters |
| Parcel label | `/parcels/:id/label` | printable label with barcode and QR |
| Parcel details | `/parcels/:id` | movement history and "Print label" for staff |

## Tests

- Unit: branch and warehouse rules (manager scoping, activate/deactivate guards), staff assignment rules, the scan service
  (every field recorded, duplicates, unexpected arrival, access by assignment, batches with mixed outcomes), inventory and
  waiting-list mapping, label building and its privacy, the domain entities and tracking-number extraction from links.
- Integration (real HTTP API, in-memory SQLite): branch and warehouse management over HTTP, staff assignment (including the
  one-active-assignment rule and history), scans end to end (single, duplicate, batch, warehouse to warehouse, unexpected
  arrival, forbidden roles, unassigned staff), the scan log and its filters, inventory and waiting lists, labels and
  lookup, the append-only guard on movements and scans, and a round trip that decodes the generated barcode and QR code.
