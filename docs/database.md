# Afno Parcel — Database design (Phase 2)

SQL Server, Entity Framework Core 10, code-first. Business tables are in schema **`parcel`**, ASP.NET Core Identity and
refresh tokens in schema **`identity`**. The EF Core configuration for each table is in
`src/NepalParcel.Infrastructure/Persistence/Configurations/` (Fluent API only — no data annotations on entities).

## Entity relationships

```mermaid
erDiagram
    Users ||--o| Customers : "online login (UserId)"
    Users ||--o| DeliveryAgents : "agent login (UserId)"
    Users ||--o{ RefreshTokens : "sessions"
    Users }o--o{ Roles : "UserRoles"

    Branches ||--o{ Warehouses : "operates"
    Branches ||--o{ DeliveryAgents : "employs"
    Branches ||--o{ ServiceAreas : "serves"
    Branches ||--o{ CODSettlements : "receives cash"

    Customers ||--o{ Parcels : "sends"
    Customers ||--o{ CustomerConsents : "decisions"
    Customers ||--o{ Payments : "pays"

    ParcelStatuses ||--o{ Parcels : "current status"
    ParcelStatuses ||--o{ ParcelStatusHistory : "from / to"
    Parcels ||--|{ ParcelItems : "contains"
    Parcels ||--|{ ParcelStatusHistory : "timeline (append-only)"
    Parcels ||--o{ DeliveryAssignments : "legs / attempts"
    Parcels ||--o{ DeliveryProofs : "evidence"
    Parcels ||--o{ Payments : "charges"
    Parcels ||--o| CODCollections : "cash collected once"
    Parcels ||--o{ NotificationOutbox : "events"
    Branches ||--o{ Parcels : "origin / destination / current"
    Warehouses ||--o{ Parcels : "currently held"

    DeliveryAgents ||--o{ DeliveryAssignments : "works"
    DeliveryAssignments ||--o{ DeliveryProofs : "captured during"
    DeliveryAgents ||--o{ CODCollections : "collects"
    CODSettlements ||--o{ CODCollections : "batches"

    Payments ||--o{ PaymentTransactions : "gateway attempts"
    NotificationOutbox ||--o{ NotificationLogs : "delivery attempts"

    ServiceAreas ||--o{ PricingRules : "from / to"
```

`WebhookEvents` and `AuditLogs` have no foreign keys by design (see below).

## Tables

| Table | Purpose | Notes |
|-------|---------|-------|
| `Branches` | Head office and branch counters | Unique `Code`; Nepal address columns; rowversion |
| `Warehouses` | Storage/sorting hubs, each belonging to a branch | Unique `Code` |
| `Customers` | Senders. Online customers link to a login; walk-ins have `UserId = NULL` | One profile per user (filtered unique index) |
| `CustomerConsents` | Consent history (terms, privacy, SMS, email, marketing) | Immutable rows; withdrawing adds a new row |
| `ParcelStatuses` | Lookup of the 11 statuses | Seeded; ids = `ParcelStatusCode` enum |
| `Parcels` | The booking: sender, receiver, route, service, weight, money, COD | **Unique `TrackingNumber`**; rowversion; check constraints |
| `ParcelItems` | What is inside a parcel | Quantity/weight check constraints |
| `ParcelStatusHistory` | Every status change with who/where/when | **Append-only**; index `(ParcelId, OccurredAtUtc)` |
| `DeliveryAgents` | Riders/drivers (linked to a login and a branch) | Unique `UserId` |
| `DeliveryAssignments` | Pickup / delivery / return tasks and attempts | Filtered unique index: one *open* assignment per parcel and type |
| `DeliveryProofs` | Signature / photo / OTP evidence | Files referenced by private `StorageKey`, never a public URL |
| `Payments` | Amount owed/paid per parcel (NPR) | `Amount > 0` check; rowversion |
| `PaymentTransactions` | Gateway attempts (eSewa, Khalti, bank…) | Unique `(Provider, ProviderReference)` for idempotency |
| `CODCollections` | Cash collected on delivery | Unique per parcel; rowversion |
| `CODSettlements` | Batch hand-over of COD cash to the accountant | Unique `ReferenceNumber`; rowversion |
| `NotificationOutbox` | Transactional outbox for SMS/email | Index `(Status, NextAttemptAtUtc)` for the worker |
| `NotificationLogs` | Delivery attempt log | Recipient stored **masked** |
| `WebhookEvents` | Inbound provider events | Unique `(Provider, EventId)` → replays detected |
| `PricingRules` | Price per weight band, service type and area (NPR) | Check constraints; end old rule + add new rule to change a price |
| `ServiceAreas` | Where we deliver, which branch serves it | Unique `Code`; index `(Province, District)` |
| `AuditLogs` | Who did what and when | **Append-only, no foreign keys** |
| `identity.*` | `Users`, `Roles`, `UserRoles`, claims, logins, tokens | ASP.NET Core Identity |
| `identity.RefreshTokens` | Hashed refresh tokens with rotation chain | Unique `TokenHash`; rowversion |

## Design rules

**Money** is always `decimal(18,2)` NPR (never `float`/`double`); weights are `decimal(10,3)` kg; percentages
`decimal(5,2)`. The domain rounds half-away-from-zero to paisa and rejects negatives. `Payment.Currency` is fixed to `NPR`.

**Time** is stored as `datetimeoffset` in **UTC** (`...AtUtc` columns). Nothing is stored in local time. Convert for
display with `NepalTime` (C#) or `formatNepalDateTime` (TypeScript) — Asia/Kathmandu, UTC+05:45, no daylight saving.

**Tracking numbers** look like `AFNK7M2Q9XT4B3`: prefix `AFN`, ten random characters from an unambiguous alphabet and a
check character that catches typing mistakes. Generated with a cryptographic RNG so they cannot be guessed in sequence.
A unique index guarantees uniqueness; on the (extremely unlikely) collision the booking code retries.

**Concurrency**: `rowversion` columns on `Branches`, `Warehouses`, `Customers`, `Parcels`, `DeliveryAgents`,
`DeliveryAssignments`, `Payments`, `CODCollections`, `CODSettlements`, `NotificationOutbox`, `PricingRules` and
`RefreshTokens`. EF Core compares them on every update; a lost race raises `ConcurrencyException`, returned as HTTP 409
(or 401 for a refresh-token race).

**History is preserved**
- Every foreign key is `ON DELETE NO ACTION` (verified: 43 foreign keys, none cascade). A parcel, customer or branch
  that has history cannot be hard-deleted; deactivate (`IsActive`) instead.
- `ParcelStatusHistory`, `PaymentTransactions`, `CustomerConsents`, `NotificationLogs` and `AuditLogs` are never updated.
- Status history stores a *snapshot* of the staff name, so it stays readable if an account is later changed.
- Status changes go through `Parcel.ChangeStatus`, which enforces the workflow (`ParcelStatusWorkflow`) — e.g. a parcel
  cannot jump from *Booked* to *Delivered* — and adds the history row in the same save.

**Enums** are stored as readable text (`'InTransit'`) so the data is understandable in SQL and safe if enum order changes.

**Indexes** cover the lookups the application needs: tracking number, sender, `(status, booked date)`, origin /
destination / current branch, receiver phone, agent workload, settlement batches, the outbox worker query, audit by
entity / user / time. Filtered unique indexes enforce "one profile per login", "one open assignment per parcel per type"
and "provider reference unique when present".

## Personal data and what may be exposed

Personal data lives in `Customers` (name, phone, email, address), `Parcels` (receiver name, phone, address),
`Users`, `NotificationOutbox` (recipient/payload) and `DeliveryProofs` (recipient name).

- Public endpoints must return only **tracking status and timeline** (no names, phones, emails or full addresses).
  Contracts for public responses will be separate DTOs, so a field cannot leak by accident.
- `/auth/me` returns only the caller's own profile. JWTs contain no phone or address.
- Logs and audit rows hold user **ids** and **masked** values (`s***@example.com`, `98******67`); `CreatedBy` /
  `UpdatedBy` are user ids, not names. Passwords and tokens are never logged or stored in clear.
- Proof images are referenced by a private storage key, never a public URL.
- Not yet done (recommended before production): column-level encryption or SQL Server Always Encrypted for phone and
  address, and a data-retention/erasure job driven by `CustomerConsents`.

## Seed data

Applied by the migration (`HasData`), so every environment gets the same ids:

- **Roles** (`identity.Roles`): SuperAdmin, BranchManager, BookingStaff, WarehouseStaff, DeliveryAgent, Accountant, Customer.
- **ParcelStatuses**: 1 Booked, 2 PickedUp, 3 ReceivedAtBranch, 4 InTransit, 5 ReceivedAtWarehouse, 6 OutForDelivery,
  7 Delivered*, 8 DeliveryFailed, 9 ReturnToSender, 10 Returned*, 11 Cancelled* (\* terminal).

No users or credentials are seeded. The only way to create the first administrator is the optional
`Bootstrap:SuperAdmin` configuration described in the README.

## Migrations

| Migration | Contents |
|-----------|----------|
| `InitialCreate` | Phase 1: `Branches` |
| `Phase2_DomainAndIdentity` | Everything above, Identity tables, indexes, constraints, seed data |

Create and apply: see the README. The model for tests (SQLite) differs only in two provider details: `rowversion`
is not generated and `nvarchar(max)` columns use SQLite's default text type.
