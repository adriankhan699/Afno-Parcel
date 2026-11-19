# Afno Parcel — Delivery agents (Phase 6)

How delivery agents are created and managed, how parcels are given to them, what an agent can and cannot do, how a delivery
is proved and paid for, and how performance is reported. There is **no GPS or location tracking** anywhere in this phase.
All timestamps are UTC (display them in Nepal time, UTC+05:45).

## Who does what

| Task | Who |
|------|-----|
| Create, edit, switch on/off, set availability of agents | `SuperAdmin`, or the `BranchManager` of the agent's branch |
| See the parcels ready for delivery, give them to agents, take them back | `SuperAdmin`, or a `BranchManager` for their own branch |
| See the delivery assignments and the performance report | same |
| Look at a proof picture (signature or photo) | staff who work with parcels (`SuperAdmin`, `BranchManager`, `BookingStaff`, `WarehouseStaff`, `Accountant`) |
| Everything on the road (accept, start, prove, collect cash, complete, fail, return) | the **delivery agent** the parcel was given to, and nobody else |

A branch manager works only with the agents of their own branch and with parcels that are at, or heading to, that branch (a
parcel whose delivery failed belongs to the branch of the agent who tried). A manager who has not been assigned to a branch
gets `403` with a message saying so.

## Delivery agents

An agent is a login (role `DeliveryAgent`) plus an agent profile plus an assignment to a branch (so the agent can work at that
branch's warehouses like any other staff member). `POST /api/v1/delivery-agents` creates all three in one step; if saving the
profile fails the login is removed again, so no half-made account is left behind.

- **Edit**: phone number, vehicle type, vehicle number (kept in capitals) and licence number. The name, login and branch do not
  change here.
- **Switch off**: the login is switched off (it cannot sign in or refresh a session) and the agent is marked unavailable.
  Refused with `422` while the agent has open assignments. **A token that is still valid stops working at once**: every agent
  request looks the agent profile up and refuses an inactive one with `403`.
- **Availability**: an agent (or a manager for them) can mark themselves unavailable; an unavailable agent cannot be given new
  parcels, but keeps and finishes the ones they have.

## Giving parcels to agents

`GET /api/v1/delivery/ready` lists parcels that are ready and have no open assignment, longest-waiting first: shipped, in
transit, arrived at a warehouse, or delivery failed (a retry). `POST /api/v1/delivery/assignments` gives one to an agent.

Rules, all enforced by the server:

- the agent must be active **and available**; the parcel must be in a state that can move to *out for delivery*;
- a parcel can have **one open delivery assignment at a time** (checked in the service, and by a database index, so two
  managers clicking at once cannot both win); a second attempt is `409`;
- every assignment has an **attempt number**: 1 the first time, 2 after a failure or a take-back, and so on; old attempts are
  kept as history;
- a branch manager can only use agents of their own branch, on parcels at or heading to their branch.

`POST /api/v1/delivery/assignments/{id}/cancel` takes a parcel back from an agent who has **not started** (reason required,
`422` once the parcel is out for delivery). Cancelling frees the parcel for another agent.

## What an agent can see and do

Everything is under `/api/v1/agent` and needs a signed-in **delivery agent with an active profile**. The agent's assignments are
found by *their own agent id*: someone else's assignment is answered with `404`, exactly as if it did not exist, for every
operation. A login with the role but no profile, or a switched-off profile, gets `403`. Managers and other staff get `403` on
the agent API too.

Delivery agents can **no longer use the generic status API** (`POST /parcels/{id}/status`), cannot read parcels, and cannot
scan: that was a Phase 4 shortcut that would have let any agent change any parcel. Their work goes through the endpoints
below, which also collect the proof and the cash.

| Step | Endpoint | What happens |
|------|----------|--------------|
| See my work | `GET /agent/me`, `GET /agent/assignments?scope=open\|history` | Summary (open jobs, delivered and failed today, cash collected today and cash in hand); open means to do, or failed and waiting to go back to a warehouse |
| Accept (optional) | `POST /agent/assignments/{id}/accept` | Assigned → Accepted |
| Take it out | `POST …/start` | Parcel → **OutForDelivery** (recorded at the warehouse it was in, with a hand-over movement, or at the agent's branch if it was on a vehicle) |
| Prove it | `POST …/delivery-code` then `POST …/proof/otp`; or `POST …/proof/file` | One-time code, signature or photo (below) |
| Collect cash | `POST …/collect-cod` | Exact amount only (below) |
| Confirm | `POST …/complete` | Parcel → **Delivered**; needs a proof, and the cash if cash is due |
| Fail it | `POST …/fail` | Parcel → **DeliveryFailed**; a reason (5–200 characters) is required |
| Take it back | `POST …/return-to-warehouse` | After a failure only: the parcel arrives at the chosen warehouse (status, movement history and audit trail all record it); kept on the agent's record as a finished *Return* task |

Each assignment the agent receives says what is possible now (`canStart`, `canFail`, `canComplete`,
`canReturnToWarehouse`), so the screen never has to guess the rules.

**Privacy.** The receiver's phone number, street address and delivery instructions are sent **only while the assignment is
open**. Finished jobs show the receiver's name and area only. The agent cannot read parcels outside their assignments.

**Failed deliveries.** The attempt stays on the record with its reason; the parcel shows *delivery attempt failed* publicly
(the reason itself is for staff only). The agent takes the parcel back to a warehouse of their own branch; a manager can give
it out again (attempt 2) to the same or another agent. If the parcel has been given out again, the old agent can no longer
take it back (`422`). A delivery cannot be failed once cash has been collected for it.

### Proof of delivery

A delivery cannot be completed without at least one proof (at most 5 per delivery):

- **Code (OTP).** `…/delivery-code` sends a 6-digit code by SMS (through the notification outbox and the background worker, template `delivery-otp`; see [notifications.md](notifications.md)) to the
  **receiver's phone on the booking**. The agent never sees it; the receiver reads it out and the agent enters it
  (`…/proof/otp`). Only a keyed hash is stored. It is valid for `Tracking:OtpLifetimeMinutes` (10), works once, locks after
  `Tracking:MaxFailedAttempts` (5) wrong guesses, and at most `Tracking:MaxRequestsPerHour` (3) codes can be sent per delivery.
  The code is bound to the parcel **and to this purpose**, so a tracking code can never be used as a delivery code. With
  `Tracking:LogCodes` on (development only) the code is written to the log, like tracking codes.
- **Signature or photo.** A multipart form (`type`, `recipientName`, `file`). A signature needs the name of the person who
  signed. The file must be a PNG, JPEG or WebP picture **by its first bytes** (the file name and the type the client claims are
  ignored), at most `Delivery:MaxProofSizeKb` (6000 KB). The picture is kept in **private storage** under a random name
  (`Delivery:ProofStoragePath`, never served as static files) and referenced by a storage key. Staff read it through
  `GET /api/v1/delivery/proofs/{id}/file`, which needs a staff sign-in and sends `Cache-Control: private, no-store` and
  `X-Content-Type-Options: nosniff`. If saving the proof fails, the stored picture is deleted again.
- Proof rows are **append-only** (a save guard refuses any edit or delete), like the status history.

In Docker the folder is `/var/afno/proofs` on a named volume (`proof-data`); back it up with the database.

### Cash on delivery

`codAmount` on a parcel is what the rider takes from the receiver: the goods money plus, for the *CashOnDelivery* payment
method, the delivery charge. If it is more than zero:

- the agent must record the cash with `…/collect-cod`, and the amount must equal `codAmount` **exactly** (otherwise `422`
  "Collect exactly NPR …"; a manager sorts out any difference, not the road);
- it can be recorded **once** per parcel (`409` afterwards; a database index enforces it too);
- the delivery cannot be completed before the cash is recorded, and cannot be marked failed after it;
- when the receiver was paying the delivery charge at the door, that payment is marked **paid**;
- the cash shows as *cash in hand* on the agent's summary until the agent hands it in; handing in, the accountant's count,
  disputes and the agent cash position are described in [payments.md](payments.md).

## The assignment follows the parcel

Whoever changes a parcel's status, however they do it, the delivery assignment keeps in step (this lives in the one shared
status changer): handing the parcel to a rider (the status API, or a warehouse scan *out for delivery*) starts a waiting
assignment; *delivered* completes it; *delivery failed* fails it (the remarks become the reason); *returned* and *cancelled*
cancel it. Moving a parcel between warehouses leaves a waiting assignment alone.

## Performance

`GET /api/v1/delivery-agents/performance?from=&to=&branchId=&deliveryAgentId=` (Nepal calendar days, default the last 30 days,
at most 366). One row per agent, counting the assignments **given in the period**, plus a totals row:

| Column | Meaning |
|--------|---------|
| Assigned / Delivered / Failed / Cancelled | assignments by outcome |
| In progress | still open (assigned, accepted or out for delivery) |
| Success rate | delivered ÷ (delivered + failed) |
| First-attempt success | among attempt-1 assignments that finished: the share delivered |
| Average delivery hours | assignment → completion, delivered ones only |
| COD collections / collected | cash collections and their total in the period (by collection time) |

Agents with no activity are listed too (rates empty). A branch manager always gets their own branch.

## API summary

| Method | Route | Who |
|--------|-------|-----|
| GET, POST | `/api/v1/delivery-agents` | managers |
| GET, PUT | `/api/v1/delivery-agents/{id}` | managers |
| POST | `/api/v1/delivery-agents/{id}/activate`, `/deactivate` | managers |
| PUT | `/api/v1/delivery-agents/{id}/availability` | managers |
| GET | `/api/v1/delivery-agents/performance` | managers |
| GET | `/api/v1/delivery/ready` | managers |
| GET, POST | `/api/v1/delivery/assignments` | managers |
| POST | `/api/v1/delivery/assignments/{id}/cancel` | managers |
| GET | `/api/v1/delivery/proofs/{proofId}/file` | `SuperAdmin`, `BranchManager`, `BookingStaff`, `WarehouseStaff`, `Accountant` |
| GET | `/api/v1/agent/me`, `/api/v1/agent/assignments`, `/api/v1/agent/assignments/{id}` | the agent |
| PUT | `/api/v1/agent/me/availability` | the agent |
| POST | `/api/v1/agent/assignments/{id}/accept`, `start`, `fail`, `delivery-code`, `proof/otp`, `proof/file`, `collect-cod`, `complete`, `return-to-warehouse` | the agent |

Errors are the usual problem responses: `400` validation, `401`, `403` wrong role / not an active agent / outside your branch,
`404` (also: not your assignment), `409` already assigned / cash already collected / already in that state, `422` a rule refused
it.

## Data model

New table `DeliveryOtps` (hashed one-time delivery codes). The Phase 2 tables are now used: `DeliveryAgents`,
`DeliveryAssignments` (filtered unique index: one open assignment per parcel and type), `DeliveryProofs` (append-only) and
`CODCollections` (unique per parcel). See [database.md](database.md).

## Web app

**Agents** (mobile first, `/agent`, role `DeliveryAgent` only; agents land here after signing in): *Today* (summary, availability,
open jobs), the job screen (tap-to-call, address, an "Open in maps" link built from the address text, parcel info, the cash
due, then a guided Start → cash → proof (code, signature pad or camera photo) → confirm, with a failed-delivery sheet and a
return-to-warehouse sheet), *History* and *Me*. Photos are shrunk in the browser before upload. No geolocation API is used.

**Managers** (`/admin`): *Agents* (list, create, edit, switch on/off, availability), *Delivery* (ready to assign; assignments
with filters, take back, proofs), *Agent performance* (period, success-rate bars, totals, CSV download).

## Tests

- Unit: the assignment, agent and code domain rules (including that a refused call changes nothing), the status changer keeping
  assignments in step, who counts as an agent (anonymous, other roles, no profile, switched off), validators, picture
  recognition, and the report's arithmetic.
- Integration (real HTTP API, in-memory SQLite, a temporary proof folder): creating and managing agents and branch scoping;
  assigning (ready list, double assignment, not-ready parcels, availability, take-back); an agent never seeing or touching
  another agent's assignment, and every other role being refused; the full successful delivery by code, signature and photo;
  the exact-cash rule and payment marking; failed deliveries, returning to a warehouse, retries and stale returns; the
  assignment following direct status changes and scans; the private proof storage (including path-safety); append-only proofs;
  the audit trail; and the performance report.
