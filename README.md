# Afno Parcel Delivery Management Platform

Courier management platform for our own parcel delivery company in Nepal: online and walk-in bookings,
tracking-number based parcel tracking, staff-driven status updates, Nepal addresses and NPR currency,
built to grow into multiple branches and warehouses.

> **Status: Phase 2 — database design and authentication.** The full relational model (21 business tables plus
> ASP.NET Core Identity) with an EF Core migration, seed data, and JWT authentication with refresh-token rotation and
> role-based authorization are in place. Parcel booking/tracking, payments and notifications **use** these tables in later
> phases. See [docs/database.md](docs/database.md) for the data model.

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
| `GET /api/v1/branches…` | Anyone (branch locations are public) |

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
| GET  | `/health/live` | Liveness probe |
| GET  | `/health/ready` | Readiness probe (database) |

Errors are always `application/problem+json`: `400` validation, `401` not signed in / bad credentials / bad refresh
token, `403` wrong role, `409` duplicate or concurrency conflict, `429` rate limited.

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
- **Phase 4** — Pricing engine (NPR, weight/zone), payments (eSewa/Khalti), COD collection and settlement.
- **Phase 5** — Notification worker (outbox → SMS/email), webhooks, delivery agents and proofs, warehouses, reporting.
