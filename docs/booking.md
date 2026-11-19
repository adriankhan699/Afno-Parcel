# Afno Parcel — Parcel booking and pricing (Phase 3)

How a parcel is booked, how its delivery charge is calculated, and which rules apply to cancelling it. All money is
NPR (`decimal(18,2)`), all weights are kilograms (`decimal(10,3)`), all timestamps are UTC (display them in Nepal
time, UTC+05:45).

## Who books

| Channel | Who | How the sender is recorded |
|---------|-----|----------------------------|
| `Online` | A signed-in `Customer` on the website | Linked to the customer's own profile. They see only their own parcels. |
| `Counter` | `BookingStaff`, `BranchManager` or `SuperAdmin` at a branch | A **walk-in customer** (no login) is created from the sender details, or reused when a walk-in with the same phone number and name already exists. |

The channel is decided by the caller's role, never by the request. Each booking stores a **snapshot** of the sender and
the receiver (name, phone, address, instructions), so a receipt stays correct if a customer later edits their profile.

## What a booking contains

| Part | Fields |
|------|--------|
| Sender and receiver | Full name, Nepali phone number, province, district, municipality, ward (1–35), street or locality, optional address instructions (max 300 characters). The district must belong to the province (the 77 districts are built in). |
| Parcel | Description (max 200), quantity (1–100 pieces), **total** weight in kg, optional length/width/height in cm (all three or none), optional declared value, optional notes. |
| Service | `Standard`, `Express` or `SameDay`. |
| Payment | `Cash`, `Esewa`, `Khalti`, `ConnectIps`, `BankTransfer` (prepaid) or `CashOnDelivery`. Optional `codAmount` (goods money to collect). |
| Hand-over | `originBranchId`: the branch where the parcel is dropped off. Pricing starts from this branch's area. |

There is **no price field** in the request. Unknown JSON properties are ignored, so a client that sends
`deliveryCharge`, `price` or `totalCharge` has no effect on what is charged (this is covered by an integration test).

## Tracking numbers

`AFN` + 10 random characters from `ABCDEFGHJKMNPQRSTUVWXYZ23456789` (no `0/O/1/I/L`, so they survive being read over the
phone) + 1 check character, e.g. `AFNK7M2Q9XT4B3`. They are generated with a cryptographic random generator, checked for
a clash before saving, and backed by a unique database index. Customers may type them with spaces, dashes or lower case.

## How the delivery charge is calculated

The calculation lives in one place, `PricingCalculator` (domain) driven by `PriceEngine` (application). Both
`POST /pricing/quote` and `POST /parcels` use it, so the quote a customer sees is what the booking is charged at that
moment. Rules are rows in `PricingRules` and can be changed without a deployment.

1. **Destination**: the receiver's province and district must match a `ServiceArea` that is serviceable, otherwise the
   booking is refused with `422` ("We do not deliver to … yet"). A municipality-level area wins over the district-wide one.
2. **Origin**: the area of the **origin branch** (a real location we control), not an address typed by the customer.
3. **Chargeable weight**: the larger of the actual weight and the volumetric weight
   (`length × width × height ÷ 5000`), rounded **up** to the next 0.5 kg.
4. **Rule**: among active rules in date for the service type, whose route matches and whose weight band covers the
   chargeable weight (a band covers weights **above** its minimum, **up to and including** its maximum), the winner is
   the most specific (names both areas, then one, then neither), then the highest `Priority`, then the newest. If no
   rule matches the booking is refused with `422`.
5. **Charge**:

   ```
   weight charge   = PricePerKg × (chargeable weight − band minimum)
   shipping charge = BasePrice + weight charge
   COD fee         = CodFeePercent % × goods amount to collect (0 when there is none)
   VAT             = VatPercent % × (shipping charge + COD fee)          (13% by default)
   total           = shipping charge + COD fee + VAT
   ```

   Every amount is rounded to paisa, halves away from zero.

Worked example (development price list): Kathmandu branch → Pokhara, `Standard`, 2.4 kg, no dimensions. Chargeable
weight 2.5 kg, band 1–5 kg (base 150, 45 per kg above 1 kg): `150 + 45 × 1.5 = 217.50`, VAT `28.28`, **total NPR 245.78**.
With NPR 4,000 goods money to collect at a 1.5% COD fee: fee `60.00`, VAT `36.08`, total `313.58`.

The booking stores the whole breakdown and the id of the rule that produced it, so **a stored price never changes**,
even after rules are switched off or replaced. To change a price, deactivate the old rule and add a new one
(`POST /pricing/rules`, `POST /pricing/rules/{id}/deactivate`, SuperAdmin only).

### Delivery estimate

Standard = the destination's typical days (minimum 1). Express = half of that, rounded up (minimum 1). SameDay = 10 hours.

### Prepaid, cash on delivery, and COD goods money

| Payment method | Who pays the delivery charge | Payment record |
|----------------|------------------------------|----------------|
| Prepaid method booked **at the counter** | The sender, now | `Paid` immediately (staff take the money or check the wallet transfer; an optional reference is stored) |
| Prepaid method booked **online** | The sender | `Pending` until counter staff confirm it (`POST /parcels/{id}/confirm-payment`). Online gateways (eSewa, Khalti, ConnectIPS) are connected in a later phase. |
| `CashOnDelivery` | The receiver, at the door | `Pending`, collected by the rider at delivery |

`codAmount` is separate: it is goods money the receiver pays on the sender's behalf, and it carries the COD fee.
`CodAmount` on the parcel (the amount the rider collects) is **goods amount + (the delivery charge, for
`CashOnDelivery` only)**. A prepaid parcel with `codAmount: 4000` collects 4000; a `CashOnDelivery` parcel with
`codAmount: 4000` and a 313.58 charge collects 4313.58.

## Cancelling

| Who | When | Result |
|-----|------|--------|
| Customer (own parcel) | Only while the status is `BookingCreated` (not yet picked up) | `Cancelled` |
| Counter staff | While `BookingCreated` or `PickedUp` (not yet shipped) | `Cancelled` |
| Anyone | Once the parcel is shipped, in transit, out for delivery, delivered, returned or already cancelled | Refused with `422` |

A reason of 5–300 characters is required and is stored in the parcel's status history (see [tracking.md](tracking.md) for the full status workflow). On cancellation a `Pending`
payment becomes `Failed` (it can no longer be paid) and a `Paid` payment is flagged `Refunded`; handing the money back
and refund transactions belong to the payments phase. The rule is a subset of what `ParcelStatusWorkflow` allows.
Customers who ask about someone else's parcel get `404`, exactly as for a parcel that does not exist.

## Searching

`GET /api/v1/parcels` returns newest first, paged (`page`, `pageSize` 1–100). `search` matches the start of a tracking
number, part of the sender's or receiver's name, or part of a phone number (3+ digits); `%` and `_` are plain
characters. Filters: `status`, `serviceType`, `paymentMethod`, `channel` (`Online`/`Counter`), `isCod`,
`originBranchId`, and `bookedFrom` / `bookedTo` (inclusive calendar days in **Nepal time**, `yyyy-MM-dd`).
Customers always see only their own parcels; staff see all.

## Receipts

`GET /api/v1/parcels/{id}/receipt` returns the receipt number (`R-<tracking number>`), the company header from
configuration (`Booking:Receipt:*`), and the full booking: tracking number, sender, receiver, contents, service,
itemised charges, payment method and status, amount to collect on delivery, and the estimated delivery. The web app
renders it as a print-ready A4 page (`/parcels/:id/receipt`, **Print** button).

## Reference data

`ServiceAreas` (one per district) and a **development** price list are inserted into empty tables when
`Database:SeedReferenceData` is `true` (Development and docker compose). The seeded prices are placeholders for local use:
nationwide Standard and Express bands, cheaper Kathmandu-valley rules, and SameDay within the valley only. Enter real
prices with `POST /pricing/rules`, and link each area to its serving branch when branches exist.

## Tests

- Unit tests (`tests/NepalParcel.UnitTests`): the pricing maths and rule selection, volumetric weight, cancellation policy,
  delivery estimates, address and district validation, every validator, and the booking, cancellation, search, payment
  confirmation and quote services with their collaborators faked.
- Integration tests (`tests/NepalParcel.IntegrationTests`): the real API over HTTP with in-memory SQLite: booking as a
  customer and at the counter, price taken from the server (client prices ignored), quote equals booking, COD, role and
  ownership checks, search, filters and paging, cancellation rules, payment confirmation, receipts, and pricing-rule
  administration.
