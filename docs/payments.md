# Afno Parcel — Payments (Phase 7)

How delivery charges are paid, how every payment is verified and recorded, how cash on delivery gets from the doorstep to the
accountant, and what the accountant can see. All amounts are NPR (`decimal(18,2)`), all timestamps UTC.

> **Test mode.** This build contains **no real eSewa or Khalti integration**. The two providers are *simulated* inside the
> application so the whole flow (initiate, pay, return, verify, receipt, reports) can be built, tried and tested without any
> provider account. Nothing in the simulation follows the real providers' request formats, and no real credentials exist
> anywhere in the code. The real gateways are added later by implementing one interface (see
> [Adding a real gateway](#adding-a-real-gateway)); everything around it is already in place.

## Payment methods

| Method | How it is paid | Who records it |
|--------|----------------|----------------|
| `Cash` (cash at the office) | The sender pays at the branch counter | Counter staff (`POST /payments/office-cash`, or at booking time) |
| `CashOnDelivery` | The receiver pays the delivery charge at the door | The delivery agent (`collect-cod`), see [delivery.md](delivery.md) |
| `Esewa`, `Khalti` | Online, through the provider | The server, after verifying with the provider |
| `ConnectIps`, `BankTransfer` | Checked by counter staff | Counter staff (`POST /parcels/{id}/confirm-payment`) |

A parcel is booked with one method and its delivery charge becomes one **payment** (`Pending` until paid). The goods money of a
COD parcel is *not* a payment: it belongs to the sender and travels as a cash collection (below).

## The payment ledger

Every movement of money is a **payment transaction**, and every transaction has:

- a **merchant reference**: our own unique reference for the attempt (`AFP` + 17 random characters, unique in the database).
  It is what we ask the provider about and what a callback must quote;
- the **provider reference**: the provider's own transaction id, stored only after verification, unique per provider;
- amount, currency (`NPR`), status (`Pending`, `Succeeded`, `Failed`), failure reason, times, and for money taken by a person,
  who recorded it.

Online attempts, failures and retries all stay in the history. Cash taken by a person is recorded as a transaction that is
complete the moment it is written, so the ledger covers every payment however it was made.

## Paying online

```
customer ── POST /payments/initiate ───────────────►  server: creates the attempt (Pending), asks the gateway to open it
customer ◄── { reference, redirectUrl } ──────────────
customer ── goes to the provider and pays ────────────────────►  provider
customer ◄── comes back to /pay/return/{provider}?reference=… ──
customer ── POST /payments/callback/{provider} { reference } ─►  server: asks the PROVIDER what happened, then decides
```

**The rules**, each enforced by the server and each covered by tests:

1. **A redirect proves nothing.** The callback carries a reference (and any provider values, which are ignored as evidence).
   The server asks the provider itself, server to server, whether the payment was completed. The website's return page shows only
   what the server answers.
2. **Never successful before verification.** A transaction becomes `Succeeded` (and the payment `Paid`) only inside the
   verification step, and only when the provider reports it completed *and*:
   - the **merchant reference** the provider reports is ours for this attempt,
   - the **amount** equals the amount we recorded when the attempt was started (exactly),
   - the **currency** is `NPR`,
   - the provider gave a **transaction reference**, and no other transaction of that provider already has it.

   Otherwise the attempt is marked `Failed` ("Verification failed: the amount does not match…"), the payment stays unpaid, and a
   `PaymentMismatch` audit entry records what was expected and what was reported. The customer can start a fresh attempt.
3. **Duplicate callbacks are harmless.** A finished attempt is final: calling the callback again (refresh, double click,
   provider retry) returns the same result and changes nothing, writes no second audit entry and asks the provider nothing.
   Two requests finishing the same attempt at once are resolved by the database's row version; the loser reports the winner's
   decision.
4. **Initiation is safe to repeat.** Starting a payment while an unexpired attempt for the same provider is open returns that
   same attempt. A new attempt closes any other open one.
5. **Attempts expire.** After `Payments:TransactionLifetimeMinutes` (30) an unpaid attempt is failed the next time it is
   verified. A payment the provider *did* complete is still accepted if verified late: the money was taken.
6. **Money taken twice** (the charge was already paid another way before the online payment completed) is recorded as a
   successful transaction, the payment is not touched, and a `PaymentDuplicate` audit entry flags it for a refund.
7. A reference that does not exist, or that belongs to another provider, looks exactly like "not found". The status and
   callback endpoints are anonymous (a customer's browser is not necessarily signed in) but rate limited, and show nothing
   personal: only status, amount, currency and tracking number.

The provider to use must be the one the parcel was booked with. Only the owner of the parcel, or counter staff, can start a
payment.

### The simulated providers (test mode)

In test mode the gateway for `Esewa` and `Khalti` is `TestPaymentGateway`, which keeps its own books (`TestGatewaySessions`) of
what a real provider would remember. Initiation opens a session and returns a link to a page of **this website**
(`/pay/test/{provider}/{reference}`) clearly marked *TEST MODE – this is not a real payment*. The tester presses **Pay**, **Fail**
or **Cancel** (`POST /payments/test-gateway/{reference}/complete`), which changes the session, exactly as a real customer paying
at a real provider would change the provider's records. Verification reads those books and nothing else. A test that sends a
forged "success" through the callback sees it ignored until the session has really been paid, and a test can corrupt the
session's amount or currency to prove the mismatch checks work.

`Payments:Mode`:

| Mode | Behaviour |
|------|-----------|
| `Test` (default) | Simulated eSewa and Khalti; the simulated provider pages and endpoints exist |
| `Live` | **Online payments are refused** (`422`, "not available right now") until a real gateway is registered; the simulated pages answer `404` |

Production must never run in `Test` mode with real parcels: set `Payments__Mode=Live`.

### Adding a real gateway

1. Implement `IPaymentGateway` (in `NepalParcel.Application/Payments/PaymentAbstractions.cs`), one class per provider:
   `InitiateAsync` opens the payment at the provider and returns where to send the customer; `VerifyAsync` calls the
   provider's **server-side verification API** (from the official documentation: <https://developer.esewa.com.np/> and
   <https://docs.khalti.com/>) and returns a `GatewayVerification` with the provider's own merchant reference, amount, currency
   and transaction id. Do not copy values from `GatewayVerifyRequest.CallbackData`: it is what the browser carried.
2. Keep credentials in user-secrets / environment variables, never in the repository or in `appsettings.json`.
3. Register the class (`services.AddScoped<IPaymentGateway, …>()` in the Infrastructure DI); the registry picks registered
   gateways up by their `Provider` name (`Esewa` / `Khalti`). Set `Payments:Mode` to `Live`.
4. The return page already passes whatever the provider put in the return URL to the callback as `providerData`; the real
   gateway may use those values only to *find* the payment at the provider.
5. Keep the checks in `PaymentService`: they apply to every gateway.

## Receipts and history

- `GET /api/v1/payments/parcel/{parcelId}`: the parcel's payments with every transaction (the owner and staff).
- `GET /api/v1/payments/{paymentId}/receipt`: the printable receipt of a **paid** payment (`422` before). Receipt number
  `PR-yyyyMMdd-XXXXXXXX` (stable once paid), company header from `Booking:Receipt`, tracking number, payer, amount, method,
  provider and provider reference, time and who received it ("Branch counter", "Delivery agent").

## Cash at the office

`POST /api/v1/payments/office-cash` `{ parcelId, amountReceived, note? }` (counter staff): only for parcels booked with `Cash`,
only while unpaid, and `amountReceived` must **equal the charge exactly** (change is given at the counter). It marks the payment
paid and writes a `Cash` transaction with the staff member. Cash taken at booking time at the counter, and payments confirmed
by hand, are in the ledger as well.

## Cash on delivery: collection, settlement, reconciliation

```
agent collects cash ──► Collected (in the agent's hand)
agent hands in      ──► settlement Submitted (cash waits for the accountant's count)
accountant counts   ──► count = total  → settlement Verified, every collection Settled
                    └─► count ≠ total  → settlement Disputed (remarks required), collections Disputed
accountant resolves ──► a disputed settlement accepted after investigation → Verified, collections Settled
```

- **Collection** (`collect-cod`, [delivery.md](delivery.md)): the exact amount due, once per parcel; it also writes the
  `CashOnDelivery` ledger entry for the delivery charge when the receiver was paying it at the door.
- **Hand-in** (`POST /agent/settlements`): all of the agent's cash that is neither handed in nor settled becomes one settlement
  with a reference `SET-yyyyMMdd-XXXXXX`. Cash is never in two settlements.
- **Count** (`POST /payments/settlements/{id}/verify` `{ countedAmount, remarks? }`): the accountant enters what was physically
  received. A match accepts it. A difference is **never absorbed silently**: remarks explaining it are required, the
  settlement becomes *disputed*, and the shortage or excess (`variance`) stays on the record.
- **Resolve** (`…/resolve` `{ remarks }`): after looking into a dispute. The variance stays; the cash is settled.
- **Agent cash position** (`GET /payments/agent-cash`, `GET /agent/cash`): collected, **in hand**, **in settlement**
  (waiting for the count), **disputed** and **settled**; the five numbers always add up.

Who: agents hand in their own cash only; `Accountant` and `SuperAdmin` work on every branch; a `BranchManager` on their own
branch only (an unassigned manager gets `403`). Agents can never count cash. Every step is audited, and no row is ever deleted.

## Accountant reports

- `GET /api/v1/payments/transactions` (filters: provider, status, parcel, branch, search by reference or tracking number, dates).
- `GET /api/v1/payments/reports/summary?from=&to=&branchId=` (Nepal calendar days, default the last 30, at most 366):
  charges **paid** in the period (by when they were paid), charges still **pending** (by booking time), failed attempts, the
  split **by method** and **by provider** (succeeded / failed / pending attempts and amounts), a **daily** series, the
  **cash-on-delivery position** (collected in the period; in hand, in settlement, disputed and settled as they stand now) and
  the **settlements** (counts and total variance).

`Accountant` is a head-office finance role: it sees every branch. A branch manager always gets their own branch.

## API summary

| Method | Route | Who |
|--------|-------|-----|
| POST | `/api/v1/payments/initiate` | the parcel's owner, counter staff |
| POST | `/api/v1/payments/callback/{provider}` | anyone (rate limited; the server verifies) |
| GET | `/api/v1/payments/status/{reference}` | anyone (rate limited) |
| GET, POST | `/api/v1/payments/test-gateway/{reference}`, `…/complete` | anyone, **test mode only** |
| GET | `/api/v1/payments/parcel/{parcelId}`, `/api/v1/payments/{paymentId}/receipt` | the owner, staff who work with parcels (incl. accountants) |
| POST | `/api/v1/payments/office-cash` | counter staff |
| GET | `/api/v1/payments/transactions`, `/reports/summary`, `/agent-cash`, `/settlements`, `/settlements/{id}` | `SuperAdmin`, `Accountant`, `BranchManager` (own branch) |
| POST | `/api/v1/payments/settlements/{id}/verify`, `/resolve` | same |
| GET, POST | `/api/v1/agent/cash`, `/api/v1/agent/settlements` | the delivery agent (own cash only) |

Errors are the usual problem responses: `400` validation, `401`, `403`, `404`, `409` already paid / already counted, `422` a
rule refused it (wrong amount, no gateway, wrong provider).

## Data model

Changed: `PaymentTransactions` gains `MerchantReference` (unique), `Currency`, `ExpiresAtUtc`, `RecordedByUserId`;
`CODSettlements` gains `CountedAmount`; cash collections now stay *collected* until the count is accepted. New table
`TestGatewaySessions` (the simulated provider's books; unused outside test mode). See [database.md](database.md).

## Web app

Customers: the Payment card on the parcel page (history, *Pay with eSewa/Khalti*), the simulated provider page, the return
page that waits for the server's verdict (and never trusts the URL), and the printable receipt. Counter staff: *Record cash
received* on the parcel page. Finance (`/admin/payments`, `/admin/payment-reports`, `/admin/cod-cash`): transactions, reports
with CSV export, agent cash and settlements (verify count, resolve). Agents: *Cash* (`/agent/cash`), with *Hand in cash*.

## Tests

- Unit: the verification rules with a fake gateway (reference, amount, currency, missing and reused provider ids, forged
  browser data, expiry, failed and cancelled, duplicate callbacks, double charge, concurrent completion), the registry in test
  and live mode, the simulated provider answering from its own books, merchant references, receipt numbers, the settlement and
  collection lifecycles, the cash-position arithmetic and all validators.
- Integration (real HTTP API, in-memory SQLite): both providers end to end (initiate → forged callback ignored → pay → verify →
  duplicate callbacks → receipt), failure, cancellation and retry, expiry, a late verification, amount and currency tampering at
  the provider, wrong-provider and unknown references, idempotent initiation, authorisation of every endpoint, cash at the
  office and at the counter, cash on delivery in the ledger, hand-in, count, dispute and resolution, the agent and accountant
  cash views, branch scoping, the transaction list and the summary report (with numbers), live mode, and the unique merchant
  reference in the database.
