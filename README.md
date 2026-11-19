# Afno Parcel Delivery Management Platform

Courier management platform for our own parcel delivery company in Nepal: online and walk-in bookings,
tracking-number based parcel tracking, staff-driven status updates, Nepal addresses and NPR currency,
built to grow into multiple branches and warehouses.

> **Status: Phase 8 — WhatsApp and SMS notifications.** Parcel events (booked, picked up, shipped, in transit, at a warehouse, out for delivery, delivered, delivery failed, returned) send WhatsApp and SMS messages to the sender and the receiver. Messages follow **consent** (a sender is messaged only if they agreed; both channels start off) and **opt-outs** (a number that replied STOP is never messaged). Each message is written to a **transactional outbox** in the same transaction as the status change, sent by a **Hangfire** background worker with **exponential-backoff retries**, and every attempt and every provider report is recorded with the provider's message id. A provider accepting a message makes it *submitted*, never *delivered*: delivered, read and failed come only from the provider's signed, idempotent webhooks. WhatsApp uses Meta's official Cloud API; SMS is a provider abstraction for Nepal gateways. **Both channels run on mock providers until you connect real ones** (nothing is sent to any phone); the steps are in [docs/notifications.md](docs/notifications.md), including an honest note that the Meta client has not been run against Meta and that no Nepal SMS gateway is implemented.
>
> **Phase 7 — payments (test mode).** Delivery charges can be paid in cash at the office, in cash on delivery, or online with eSewa and Khalti. A common gateway abstraction starts and verifies online payments; the server never trusts a browser redirect and accepts a payment only after asking the provider and checking the merchant reference, amount and currency. Every attempt is kept as a ledger transaction with the provider's reference; duplicate callbacks change nothing. There are receipts, cash-on-delivery settlement with agent cash reconciliation, and accountant reports. **eSewa and Khalti are simulated inside the application** (no real provider is called, no credentials exist); real gateways are added later behind the same interface. See [docs/payments.md](docs/payments.md).
>
> **Phase 6 — delivery agents.** Admins and branch managers create delivery agents and give parcels to them. Agents use a mobile-friendly screen to see only their own parcels, take them out for delivery, collect cash on delivery, prove delivery with a one-time code, a signature or a photo, mark a delivery as failed with a reason, and take failed parcels back to a warehouse. Admins see each agent's delivery performance. There is no GPS tracking. See [docs/delivery.md](docs/delivery.md).
>
> **Phase 5 — branches, warehouses and scanning.** Branches and warehouses can be created, edited and switched off; staff are assigned to the branch (and warehouse) they work at; warehouse staff scan barcodes or QR codes to record every arrival, dispatch and hand-over; each scan records the parcel, warehouse, staff member, scan type, time and remarks, and a repeated scan is logged as a duplicate without creating a second movement. Warehouses show what they hold and what is on its way, waiting-for-pickup and waiting-for-dispatch lists exist, and every parcel has a printable label with a Code 128 barcode and a QR code. Admin screens cover branches, warehouses, staff, inventory, scanning and the scan log. See [docs/network.md](docs/network.md).
>
> **Phase 4 — status tracking.** Staff move parcels through a controlled workflow (booking created → picked up → shipped → in transit → arrived at warehouse → out for delivery → delivered, with failure, return and cancel paths). Every change adds an append-only history row (previous status, new status, staff user, branch or warehouse, time, remarks). Customers track parcels publicly without seeing any personal data, and get detailed tracking with a one-time code sent to the receiver or by signing in. Warehouse staff have a scan API. See [docs/tracking.md](docs/tracking.md).
>
> **Phase 3 — parcel booking management.** Customers book parcels on the website and counter staff book for
> walk-in customers. Every booking gets a unique tracking number and a delivery charge that the **server** calculates from
> configurable pricing rules (prepaid and cash-on-delivery, VAT, volumetric weight). Bookings can be searched, filtered,
> paged, cancelled under business rules, and printed as a receipt. See [docs/booking.md](docs/booking.md).
> Phase 2 (database and authentication) is described in [docs/database.md](docs/database.md). Status updates and public
> tracking, payment gateways and notifications **use** these tables in later phases.

## Tech stack

| Layer      | Technology |
|------------|------------|
| Backend    | .NET 10, ASP.NET Core Web API (controllers) |
| Data       | SQL Server, Entity Framework Core 10 (Fluent API, migrations) |
| Validation | FluentValidation |
| Logging    | Serilog (structured JSON to stdout, correlation IDs) |
| API docs   | Swagger / OpenAPI (Swashbuckle) |
| Frontend   | React 19, TypeScript, Vite, Tailwind CSS v4, React Router |
| Tests      | xUnit, NSubstitute, WebApplicationFactory + SQLite; Vitest |
| Deployment | Docker / docker compose |

## Architecture

Clean Architecture, organised as a **modular monolith**: each business module (Branches today; Parcels,
Tracking, Pricing… later) owns a folder in every layer, so modules stay cohesive and can be split out later.

```
NepalParcel.slnx
├── src/
│   ├── NepalParcel.Domain          Entities, value objects (Money/NPR, NepalAddress, Province), invariants. No dependencies.
│   ├── NepalParcel.Contracts       Request/response DTOs exposed by the API. No dependencies.
│   ├── NepalParcel.Application     Use-case services, validators, repository/UoW abstractions → Domain, Contracts
│   ├── NepalParcel.Infrastructure  EF Core DbContext, Fluent configurations, migrations, repositories → Application
│   └── NepalParcel.API             Controllers, middleware, exception handling, DI composition root → all
├── tests/
│   ├── NepalParcel.UnitTests         Domain + application logic
│   └── NepalParcel.IntegrationTests  Full HTTP pipeline via WebApplicationFactory (SQLite in-memory)
└── frontend/                       React + Vite + TypeScript + Tailwind
```

Dependency rule: `API → Infrastructure → Application → Domain`; nothing points outward.

Key conventions

- **Controllers are thin** — they call an application service (`IBranchService`) and return its DTO.
- **DTOs only** at the boundary (`NepalParcel.Contracts`); entities never leave the Application layer.
- **Validation** in FluentValidation validators (`*Validator`) plus domain invariants in factory methods.
- **Errors** are RFC 9457 `application/problem+json` produced centrally by `GlobalExceptionHandler`:
  `RequestValidationException → 400`, `NotFoundException → 404`, `ConflictException → 409`,
  `DomainException → 422`, anything else → `500` (logged, details hidden). Every response includes
  `traceId` and `correlationId`.
- **EF Core** configuration via `IEntityTypeConfiguration<T>` classes (no data annotations on entities),
  schema `parcel`, enums stored as strings, audit columns stamped by `AuditableEntityInterceptor`.
- **IDs** are UUID v7 generated in the domain.
- **Async** end to end with `CancellationToken` propagation.

### Adding a new module

1. `Domain/<Module>/` — entities and value objects.
2. `Contracts/<Module>/` — request/response records.
3. `Application/<Module>/` — `I<Module>Service`, service, validators, `I<Module>Repository`; register in `Application/DependencyInjection.cs`.
4. `Infrastructure/Persistence/Configurations/` + `Repositories/`; register in `Infrastructure/DependencyInjection.cs`; add a migration.
5. `API/Controllers/<Module>Controller.cs`.

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) (pinned via `global.json`)
- Node.js 22.12+ and npm
- SQL Server: Docker Desktop **or** a local instance (SQL Server Express on Windows with Windows authentication works)

## Quick start with Docker (recommended)

```bash
cp .env.example .env          # then edit MSSQL_SA_PASSWORD
docker compose up --build
```

| Service    | URL |
|------------|-----|
| Frontend   | http://localhost:5173 |
| API        | http://localhost:5080 |
| Swagger UI | http://localhost:5080/swagger |
| Health     | http://localhost:5080/health/live, `/health/ready` |
| SQL Server | `localhost,1433` (user `sa`) |

The API applies EF Core migrations automatically on startup in this development setup. The frontend
container runs the Vite dev server with your `frontend/` folder mounted, so edits hot-reload.

## Running locally without Docker

### 1. Configure secrets (user-secrets — never in appsettings)

Two values are **required**. The API fails at startup with a clear message if either is missing.

```bash
cd src/NepalParcel.API

# 1) Database. SQL Server Express with Windows authentication (replace <MACHINE> with your computer name):
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=<MACHINE>\SQLEXPRESS;Database=AfnoParcel;Trusted_Connection=True;TrustServerCertificate=True"
# ...or SQL Server in Docker / remote with a SQL login:
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost,1433;Database=AfnoParcel;User Id=sa;Password=<password>;Encrypt=True;TrustServerCertificate=True"

# 2) JWT signing key: generate YOUR OWN random value (64+ characters). There is no default and none is stored in the repo.
#    PowerShell:  [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
#    Bash:        openssl rand -base64 48
dotnet user-secrets set "Jwt:SigningKey" "<paste the generated value>"
```

Optional — create the first **SuperAdmin** on startup (nothing is created unless you supply both values; the password
is yours to choose and is never logged):

```bash
dotnet user-secrets set "Bootstrap:SuperAdmin:Email" "<admin email>"
dotnet user-secrets set "Bootstrap:SuperAdmin:Password" "<strong password: 10+ chars, upper + lower + digit>"
```

### 2. Run the API

```bash
dotnet tool restore
dotnet run --project src/NepalParcel.API --launch-profile http
```

In `Development` the API applies pending migrations at startup and serves Swagger at
http://localhost:5080/swagger.

### 3. Run the frontend

```bash
cd frontend
npm install
npm run dev
```

The Vite dev server proxies `/api` and `/health` to `http://localhost:5080` (override with
`VITE_API_PROXY_TARGET` in `frontend/.env.local`).

## Database migrations

```bash
dotnet tool restore
# add a migration
dotnet ef migrations add <Name> --project src/NepalParcel.Infrastructure --startup-project src/NepalParcel.Infrastructure --output-dir Persistence/Migrations
# apply to a database (design-time factory reads this variable)
NEPALPARCEL_DESIGN_CONNECTION="<connection string>" dotnet ef database update --project src/NepalParcel.Infrastructure --startup-project src/NepalParcel.Infrastructure
# production: generate an idempotent script or bundle and run it as a release step
dotnet ef migrations script --idempotent --project src/NepalParcel.Infrastructure --startup-project src/NepalParcel.Infrastructure -o migrate.sql
```

## Build and test

```bash
dotnet restore NepalParcel.slnx
dotnet build NepalParcel.slnx
dotnet test NepalParcel.slnx

cd frontend
npm ci
npm run lint
npm test
npm run build
```

Integration tests boot the real API with an in-memory SQLite database, so they need neither Docker nor SQL Server.

## Configuration and secrets

| Setting | Where it comes from |
|---------|---------------------|
| `ConnectionStrings:DefaultConnection` | user-secrets (dev), `ConnectionStrings__DefaultConnection` env var / secret store (Docker, prod). Startup fails fast with a clear message if missing. |
| `Jwt:SigningKey` | **Required secret**, 32+ characters (use 64+ random). user-secrets / `Jwt__SigningKey` env var / secret store. No default; startup fails if missing or short. |
| `Jwt:Issuer`, `Jwt:Audience` | Non-secret identifiers in `appsettings.json` (`afno-parcel-api` / `afno-parcel-clients`). |
| `Jwt:AccessTokenMinutes`, `Jwt:RefreshTokenDays` | Token lifetimes. Defaults: 15 minutes and 7 days. |
| `Bootstrap:SuperAdmin:Email` / `:Password` | Optional first-run SuperAdmin. Both must be set or nothing is created. Remove the password after the first start. |
| `RateLimiting:AuthPermitPerMinute` | Requests per IP per minute for register/login/refresh/logout (default 10). Behind a reverse proxy, configure forwarded headers so the client IP is seen. |
| `RateLimiting:TrackingPermitPerMinute`, `RateLimiting:TrackingOtpPermitPerMinute` | Public tracking lookups (default 60) and one-time code requests/verifications (default 10) per client IP per minute. |
| `Notifications:*` | WhatsApp and SMS. `Notifications:WhatsApp:Provider` / `Notifications:Sms:Provider` are `Mock` by default (nothing is sent). For Meta set `Provider` to `Meta` and the **secrets** `Notifications:WhatsApp:PhoneNumberId`, `AccessToken`, `AppSecret` and `VerifyToken` with user-secrets (the API refuses to start with `Meta` and a secret missing). Also `NotifyReceivers`, and `Outbox:MaxAttempts` (5), `BaseDelaySeconds` (30), `MaxDelaySeconds` (3600). See [docs/notifications.md](docs/notifications.md). |
| `Hangfire:Mode` | The background worker that sends messages: `SqlServer` (default; tables in schema `hangfire` of the application database), `InMemory` (the Development default) or `Disabled` (nothing is sent). `Hangfire:DashboardEnabled` serves a read-only `/hangfire` to requests from the same machine only; leave it off in production. |
| `Database:SeedNotificationTemplates` | `true` (default): adds the default message texts that are missing; never overwrites edited ones. |
| `Payments:Mode` | `Test` (default): simulated eSewa and Khalti with a simulated provider page. `Live`: online payments are **refused** until real gateways are registered (none is built in). **Production must use `Live`.** Also `Payments:TransactionLifetimeMinutes` (30) and `Payments:TestMerchantName`. See [docs/payments.md](docs/payments.md). |
| `Delivery:*` | `ProofStoragePath` (private folder for signature and photo proofs; in Docker `/var/afno/proofs` on a volume, **back it up** with the database), `MaxProofSizeKb` (6000), `MaxProofsPerAssignment` (5). Never served as static files. See [docs/delivery.md](docs/delivery.md). |
| `Tracking:PublicBaseUrl` | Public address of the customer website (default `http://localhost:5173`). The QR code on a parcel label links to `{PublicBaseUrl}/track/{trackingNumber}`: set it to your real domain in production. |
| `Tracking:*` | One-time code lifetime (10 min), wrong guesses allowed (5), codes per parcel per hour (3), access token lifetime (15 min). `Tracking:LogCodes` writes each code to the log so developers can test without an SMS provider: **on only in `appsettings.Development.json`, never in production**. See [docs/tracking.md](docs/tracking.md). |
| `RateLimiting:BookingPermitPerMinute` | Quotes, bookings, cancellations and payment confirmations per signed-in user (or IP when anonymous) per minute (default 30). |
| `Booking:*` | VAT (`VatPercent`, default 13), volumetric divisor (5000), weight step (0.5 kg), `MaxWeightKg`, `MaxDimensionCm`, `MaxDeclaredValue`, `MaxCodAmount`, and the receipt header (`Booking:Receipt:CompanyName`, `CompanyPhone`, `CompanyTaxNumber`). Not secrets. See [docs/booking.md](docs/booking.md). |
| `Database:SeedReferenceData` | `true` only in `appsettings.Development.json` / compose: fills **empty** `ServiceAreas` (77 districts) and `PricingRules` (development price list). Keep `false` in production and enter real prices through `/api/v1/pricing/rules`. |
| `Database:ApplyMigrationsOnStartup` | `true` only in `appsettings.Development.json` / compose. Keep `false` in production. |
| `Cors:AllowedOrigins` | Allowed browser origins. Empty by default (same-origin via reverse proxy). |
| `Swagger:Enabled` | On in Development, off otherwise unless explicitly enabled. |
| `Serilog:*` | Log levels, sinks and formatting. Production default: compact JSON to stdout. |

Rules followed by this repo:

- `appsettings*.json` contain **no secrets**; `.env` files are git-ignored, only `.env.example` is committed.
- Only `VITE_*` variables reach the browser bundle — never place secrets in them.
- Docker images run as non-root users.
- `nuget.config` pins nuget.org with package source mapping so restores are reproducible on any machine.
- Production should use a least-privilege SQL login (not `sa`) and terminate TLS at the reverse proxy / ingress.

## Authentication and authorization

ASP.NET Core Identity stores users and roles (schema `identity`); passwords are salted and hashed with PBKDF2 by
Identity's password hasher and are never stored or returned in clear text. Policy: 10+ characters with upper-case,
lower-case and a digit; 5 failed sign-ins lock the account for 15 minutes.

**Tokens**

- **Access token** — JWT (HS256), 15 minutes. Contains `sub`, `email`, `name`, `role` only (no phone or address).
  Send as `Authorization: Bearer <token>`.
- **Refresh token** — random 64-byte opaque value, 7 days, **single use**. Only its SHA-256 hash is stored.
  Every refresh returns a *new* pair and revokes the old refresh token (**rotation**). If an already-used token is
  presented again, the whole session (token family) is revoked and the event is audit-logged — this detects theft.
  Concurrent attempts to rotate the same token are resolved by a rowversion check: only one wins.
- Keep the refresh token out of `localStorage` where possible (memory, or an HttpOnly cookie via your own BFF).

**Roles**: `SuperAdmin`, `BranchManager`, `BookingStaff`, `WarehouseStaff`, `DeliveryAgent`, `Accountant`, `Customer`
(seeded by the migration). Public self-registration always creates a `Customer`. Staff accounts are created by a
SuperAdmin; the first SuperAdmin comes from the optional bootstrap configuration above.

| Endpoint | Who can call it |
|----------|-----------------|
| `POST /api/v1/auth/register`, `login`, `refresh`, `logout` | Anyone (rate limited) |
| `GET /api/v1/auth/me` | Any signed-in user (own profile only) |
| `POST /api/v1/admin/users` | `SuperAdmin` |
| `POST /api/v1/branches` | `SuperAdmin`, `BranchManager` |
| `PUT /api/v1/branches/{id}`, `…/activate`, `…/deactivate`; warehouse create/update/activate/deactivate | `SuperAdmin`, or the `BranchManager` of that branch |
| `GET /api/v1/staff`, `PUT/DELETE /api/v1/staff/{userId}/assignment` | `SuperAdmin`, `BranchManager` (own branch only) |
| `GET /api/v1/staff/me/assignment` | Any staff member |
| `GET /api/v1/warehouses/{id}/inventory`, `GET /api/v1/inventory/waiting`, `GET /api/v1/scans`, `GET /api/v1/parcels/{id}/movements`, `GET /api/v1/parcels/lookup` | `SuperAdmin`, `BranchManager`, `BookingStaff`, `WarehouseStaff`, `Accountant` |
| `GET /api/v1/parcels/{id}/label` | `Customer` (own parcels), staff who work with parcels |
| `/api/v1/delivery-agents…`, `/api/v1/delivery/ready`, `/api/v1/delivery/assignments…` | `SuperAdmin`, or the `BranchManager` for their own branch |
| `GET /api/v1/delivery/proofs/{id}/file` | `SuperAdmin`, `BranchManager`, `BookingStaff`, `WarehouseStaff`, `Accountant` |
| `/api/v1/agent/…` | `DeliveryAgent` only, and only for the assignments given to them |
| `/api/v1/notifications/messages`, `…/summary`, `…/providers`, `…/templates`, `…/opt-outs` (read) | `SuperAdmin`, `BranchManager`; changing templates, opt-outs, requeue and cancel: `SuperAdmin` only |
| `GET/PUT /api/v1/notifications/preferences` | `Customer` (their own SMS and WhatsApp choices) |
| `GET/PUT /api/v1/notifications/consent/parcel/{id}`, `GET /api/v1/parcels/{id}/notifications` | Counter staff (sender's consent); staff who read parcels |
| `GET/POST /api/v1/webhooks/whatsapp` | Anyone (rate limited), but every call must carry a valid Meta signature or it is refused |
| `/api/v1/notifications/mock/…`, `POST /api/v1/webhooks/mock` | `SuperAdmin`, and only while a mock provider is in use (404 otherwise) |
| `POST /api/v1/payments/initiate` | The parcel's owner (`Customer`) or counter staff |
| `POST /api/v1/payments/callback/{provider}`, `GET /api/v1/payments/status/{reference}` | Anyone (rate limited): the server verifies with the provider, nothing from the browser is believed |
| `POST /api/v1/payments/office-cash` | Counter staff |
| `GET /api/v1/payments/parcel/{id}`, `…/{paymentId}/receipt` | The owner, and staff who work with parcels (including `Accountant`) |
| `GET /api/v1/payments/transactions`, `…/reports/summary`, `…/agent-cash`, `…/settlements…` | `SuperAdmin`, `Accountant`, `BranchManager` (own branch only) |
| `GET/POST /api/v1/agent/cash`, `/api/v1/agent/settlements` | `DeliveryAgent` (own cash only) |
| `GET /api/v1/branches…`, `GET /api/v1/service-areas`, `POST /api/v1/pricing/quote` | Anyone (public information; the quote is rate limited) |
| `POST /api/v1/parcels`, `POST /api/v1/parcels/{id}/cancel` | `Customer` (own parcels), counter staff: `BookingStaff`, `BranchManager`, `SuperAdmin` |
| `GET /api/v1/parcels…` (list, details, tracking lookup, receipt) | `Customer` (own parcels only), `SuperAdmin`, `BranchManager`, `BookingStaff`, `WarehouseStaff`, `Accountant` (all parcels) |
| `POST /api/v1/parcels/{id}/confirm-payment` | Counter staff |
| `GET /api/v1/tracking/{number}`, `POST …/otp`, `POST …/verify` | Anyone (rate limited; no personal data in the answers) |
| `GET /api/v1/tracking/{number}/details` | A receiver with a verified-code token (`X-Tracking-Access`), or a signed-in sender (own parcels) or staff |
| `POST /api/v1/parcels/{id}/status`, `…/tracking/{number}/status` | `SuperAdmin`, `BranchManager`, `BookingStaff`, `WarehouseStaff`; each role only for the statuses its job needs. Delivery agents use the agent API instead |
| `POST /api/v1/scans`, `/scans/batch` | `SuperAdmin`, or `BranchManager` / `WarehouseStaff` assigned to that warehouse's branch |
| `GET /api/v1/warehouses` / `POST /api/v1/warehouses` | Staff / `SuperAdmin`, `BranchManager` of the branch |
| `GET /api/v1/pricing/rules` | `SuperAdmin`, `BranchManager`, `Accountant` |
| `POST /api/v1/pricing/rules`, `…/{id}/deactivate` | `SuperAdmin` |

## API overview

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/v1/auth/register` | Register a customer account and sign in (201) |
| POST | `/api/v1/auth/login` | Sign in with email + password (200) |
| POST | `/api/v1/auth/refresh` | Rotate refresh token, get a new token pair (200) |
| POST | `/api/v1/auth/logout` | Revoke the session; always 204 |
| GET  | `/api/v1/auth/me` | Current user profile |
| POST | `/api/v1/admin/users` | Create a staff account with a role (SuperAdmin) |
| GET  | `/api/v1/branches?page=1&pageSize=20` | Paged list of branches |
| GET  | `/api/v1/branches/{id}` | Branch by id |
| POST | `/api/v1/branches` | Create branch / head office (SuperAdmin, BranchManager) |
| PUT, POST | `/api/v1/branches/{id}`, `…/activate`, `…/deactivate` | Edit a branch, switch it on or off |
| POST | `/api/v1/pricing/quote` | Delivery charge and estimate for a parcel, without booking (public) |
| GET  | `/api/v1/service-areas` | The 77 districts we deliver to, with typical delivery days (public) |
| POST | `/api/v1/parcels` | Book a parcel (201). The charge is calculated by the server; any price sent is ignored |
| GET  | `/api/v1/parcels?search=&status=&serviceType=&paymentMethod=&channel=&isCod=&originBranchId=&bookedFrom=&bookedTo=&page=&pageSize=` | Search, filter and page parcels (newest first) |
| GET  | `/api/v1/parcels/{id}`, `/api/v1/parcels/tracking/{trackingNumber}` | Parcel details with its status history |
| GET  | `/api/v1/parcels/{id}/receipt` | Printable booking receipt data |
| POST | `/api/v1/parcels/{id}/cancel` | Cancel a booking (reason required; rules in [docs/booking.md](docs/booking.md)) |
| POST | `/api/v1/parcels/{id}/confirm-payment` | Mark a pending prepaid charge as paid (counter staff) |
| GET/POST | `/api/v1/pricing/rules`, `/api/v1/pricing/rules/{id}/deactivate` | List, add and switch off pricing rules |
| GET  | `/api/v1/tracking/{trackingNumber}` | Public tracking: status, districts, estimate, timeline (no personal data) |
| POST | `/api/v1/tracking/{trackingNumber}/otp` | Send a 6-digit code to the receiver (always 202, reveals nothing) |
| POST | `/api/v1/tracking/{trackingNumber}/verify` | Check the code, get a short-lived `X-Tracking-Access` token |
| GET  | `/api/v1/tracking/{trackingNumber}/details` | Detailed tracking (token, or signed-in sender/staff) |
| POST | `/api/v1/parcels/{id}/status` | Staff: move a parcel to its next status (workflow, role and data rules enforced) |
| POST | `/api/v1/scans`, `/api/v1/scans/batch` | Warehouse scans: Arrival, Dispatch, OutForDelivery (batch up to 100). Repeats answer `Duplicate` and change nothing |
| GET | `/api/v1/scans` | The scan log, with filters |
| GET/POST/PUT | `/api/v1/warehouses`, `/api/v1/warehouses/{id}`, `…/activate`, `…/deactivate` | List, register, edit, switch on or off |
| GET | `/api/v1/warehouses/{id}/inventory`, `/api/v1/inventory/waiting` | What a warehouse holds and expects; parcels waiting for pickup or dispatch |
| GET, PUT, DELETE | `/api/v1/staff`, `/api/v1/staff/{userId}/assignment`, `/api/v1/staff/me/assignment` | Who works where |
| GET | `/api/v1/parcels/{id}/movements`, `/api/v1/parcels/{id}/label`, `/api/v1/parcels/lookup?code=` | Movement history; printable label with barcode and QR code; find a parcel from scanned text |
| POST | `/api/v1/payments/initiate`, `/api/v1/payments/callback/{provider}` | Start an online payment (eSewa, Khalti); the browser returns here and the server verifies |
| GET | `/api/v1/payments/parcel/{id}`, `/api/v1/payments/{paymentId}/receipt`, `/api/v1/payments/status/{reference}` | Payment history, receipt, attempt status |
| POST | `/api/v1/payments/office-cash` | Record cash taken at the office |
| GET | `/api/v1/payments/transactions`, `/api/v1/payments/reports/summary` | Accountant: transaction list and summary report |
| GET, PUT | `/api/v1/notifications/preferences`, `/api/v1/notifications/consent/parcel/{parcelId}` | Whether the sender agreed to SMS and WhatsApp updates |
| GET | `/api/v1/notifications/messages`, `…/messages/{id}`, `…/summary`, `…/providers`, `/api/v1/parcels/{id}/notifications` | The messages, what happened to each, and counts by channel (submitted, sent, delivered, read, failed, skipped) |
| POST | `/api/v1/notifications/messages/{id}/requeue`, `…/cancel` | Give a dead message another chance / stop a waiting one |
| GET, PUT, POST | `/api/v1/notifications/templates`, `…/templates/{id}`, `…/templates/preview` | Message texts |
| GET, POST, DELETE | `/api/v1/notifications/opt-outs`, `…/opt-outs/{id}` | Numbers that asked not to be messaged |
| GET, POST | `/api/v1/webhooks/whatsapp` | Meta's endpoint check and signed delivery reports |
| GET, POST | `/api/v1/notifications/mock/messages`, `…/mock/messages/{providerMessageId}/status` | Mock provider's test inbox and simulated reports |
| GET, POST | `/api/v1/payments/agent-cash`, `/api/v1/payments/settlements…`, `/api/v1/agent/settlements` | Cash on delivery: agent cash position, hand-in, count, dispute, resolve |
| GET, POST, PUT | `/api/v1/delivery-agents`, `/api/v1/delivery-agents/{id}`, `…/activate`, `…/deactivate`, `…/availability` | Create and manage delivery agents |
| GET | `/api/v1/delivery-agents/performance` | Delivery performance per agent |
| GET, POST | `/api/v1/delivery/ready`, `/api/v1/delivery/assignments`, `…/assignments/{id}/cancel` | Parcels ready for delivery; give a parcel to an agent; take it back |
| GET, PUT, POST | `/api/v1/agent/me`, `/api/v1/agent/assignments…` | The agent's own work: accept, start, prove, collect cash, complete, fail, return to warehouse |
| GET  | `/health/live` | Liveness probe |
| GET  | `/health/ready` | Readiness probe (database) |

Errors are always `application/problem+json`: `400` validation, `401` not signed in / bad credentials / bad refresh
token, `403` wrong role, `404` not found (also used when a customer asks for someone else's parcel), `409` duplicate or
concurrency conflict, `422` a business rule refused the request (e.g. "we do not deliver there yet", "too late to
cancel"), `429` rate limited.

Time: every timestamp in the API and database is **UTC** (`...Z` / `datetimeoffset`). Convert for display with
`NepalTime` (backend) or `formatNepalDateTime` (frontend) — Asia/Kathmandu, UTC+05:45, no daylight saving.
Money: NPR as `decimal(18,2)` (weights `decimal(10,3)`).

Example request:

```json
POST /api/v1/branches
{
  "code": "KTM01",
  "name": "Kathmandu Main Branch",
  "type": "Branch",
  "address": {
    "province": "Bagmati",
    "district": "Kathmandu",
    "municipality": "Kathmandu Metropolitan City",
    "wardNumber": 10,
    "streetOrTole": "New Baneshwor",
    "postalCode": "44600"
  },
  "phoneNumber": "01-4123456",
  "email": "ktm@example.com"
}
```

## Roadmap

- **Phase 1** — Foundation (done).
- **Phase 2** — Database design, EF Core, authentication (done).
- **Phase 3** — Parcels & bookings (online + walk-in), tracking-number issuing, status updates by staff, public tracking
  (status and timeline only — never contact details), frontend login.
- **Phase 4** — Status tracking: controlled workflow, append-only history, public and OTP-verified tracking (done).
- **Phase 5** — Branch and warehouse management, staff assignments, barcode/QR scanning, movement history, inventory and labels (done).
- **Phase 6** — Delivery agents: assignment, mobile agent screens, proof of delivery, cash on delivery collection, failed deliveries and performance (done).
- **Phase 7** — Payments in test mode: gateway abstraction, simulated eSewa and Khalti, verification, ledger, receipts, COD settlement and reconciliation, accountant reports (done).
- **Phase 8** — WhatsApp and SMS notifications: consent and opt-outs, transactional outbox, Hangfire worker with exponential backoff, attempt log, signed idempotent webhooks, Meta Cloud API client and SMS provider abstraction, mock providers, admin and customer screens (done).
- **Later** — Real eSewa and Khalti gateways (replacing the simulation), a Nepal SMS gateway behind `ISmsProvider`, email notifications.
