# Lupira Tasks API

A self-hosted, event-sourced backend for shared task and shopping lists. One .NET 10 service is the
single source of truth in PostgreSQL and exposes the same data over four surfaces — a fine-grained
REST API for apps, a Model Context Protocol (MCP) endpoint for AI agents, account-less public share
links, and CalDAV (VTODO) for native task clients.

It is offline-first by design: clients mint their own ids and edits, replay them with idempotency
keys after reconnecting, and the server resolves concurrent edits with per-field last-writer-wins
(LWW). See [docs/architecture.md](docs/architecture.md) for the design in depth.

## Surfaces

All four surfaces are thin adapters over **one** Marten event store — there is no second source of
truth, so an event written through any surface is immediately visible through the others.

| Surface | Route(s) | For |
| --- | --- | --- |
| **REST** | `/lists`, `/lists/{id}/items`, `/lists/{id}/sync`, `/lists/{id}/shares`, `/me`, `/users` | App clients — fine-grained CRUD + offline sync (client-supplied GUIDv7 ids, fractional-index `sortOrder`, `Idempotency-Key`, per-field LWW) |
| **MCP** | `/mcp` (Streamable HTTP) | AI agents — intent-shaped tools that mint ids/sort keys/command ids server-side |
| **Share links** | `/shared/{token}` | Account-less public access to one list (read or read/write, optional expiry, revocable) |
| **CalDAV** | `/dav/{**path}`, discovery at `/.well-known/caldav` | Native task apps over VTODO (e.g. DAVx5) |

REST and MCP are the primary surfaces; share links and CalDAV are secondary.

### MCP tools

`list_my_lists`, `create_list`, `find_tasks`, `add_task`, `complete_task`, `reopen_task`,
`update_task`, `share_list`, `create_share_link`, `list_share_links`, `revoke_share_link`. Each calls
the same application services as the REST handlers, so the surfaces never diverge.

## Authentication

| Scheme | Used by | Notes |
| --- | --- | --- |
| OIDC JWT bearer | REST + MCP (default) | `Authorization: Bearer <token>`. The subject claim is `email`; the role claim is `groups`. Issuer + audience are validated; both are required at startup. |
| Share token | `/shared/{token}` | The opaque token in the URL is the credential — no account needed. |
| HTTP Basic → LDAP | `/dav` (CalDAV) | DAVx5-class clients can't present a JWT, so the DAV surface binds Basic credentials against an LDAP directory. |
| `X-Dev-User` header | local development only | Authenticates as any email without an identity provider. Registered **only** when `ASPNETCORE_ENVIRONMENT=Development`. |

Identity is the email claim from the bearer token; the service holds no password store and no API
keys. A headless agent authenticates by presenting a member-scoped bearer token exactly as a user
does (how that token is obtained is an identity-provider concern, not part of this service).

## API documentation

- OpenAPI document: `GET /openapi/v1.json`
- Scalar reference UI: `GET /scalar` (the root `/` redirects here)

## Tech stack

| Component | Version |
| --- | --- |
| .NET | 10 (`net10.0`) |
| Marten (event store / Postgres) | 9.8.2 |
| Ical.Net (VTODO) | 5.2.2 |
| Microsoft.AspNetCore.OpenApi | 10.0.9 |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.9 |
| Scalar.AspNetCore | 2.16.4 |
| ModelContextProtocol.AspNetCore | 1.4.0 |
| System.DirectoryServices.Protocols (LDAP) | 10.0.9 |
| OpenTelemetry (hosting / OTLP exporter) | 1.16.0 |
| OpenTelemetry instrumentation (AspNetCore / Http / Runtime) | 1.15.x |
| Tests | xunit 2.9.3, Testcontainers.PostgreSql 4.x |

## Project layout

Two projects in one solution (`LupiraTasksApi.slnx`), split so ASP.NET cannot leak into the domain:

- **`src/LupiraTasksApi.Core/`** — the bounded context (a plain class library). Holds `Domain/`
  (event-sourced aggregates + pure LWW logic), `Application/` (transport-neutral services, `Caller`,
  `OpResult`), `Dtos/`, `Mappers/`, `Data/` (Marten registrations + idempotency ledger), `Ical/`
  (VTODO mapping), and `Auth/AccessResolver.cs`. Depends only on Marten — no ASP.NET reference.
- **`src/LupiraTasksApi/`** — the thin ASP.NET host. Holds `Program.cs`, the surface adapters
  (`Endpoints/` + `Handlers/`, `Mcp/`, `Dav/`, the `/shared` endpoints), HTTP concerns (`Http/`:
  result mapping, the `Idempotency-Key` reader), the auth handlers, plus OpenTelemetry, rate
  limiting, OpenAPI, and health probes.
- **`tests/`** — `LupiraTasksApi.Tests` (xunit unit tests, no infrastructure) and
  `LupiraTasksApi.IntegrationTests` (HTTP end-to-end against a Testcontainers Postgres).

## Run locally

Prerequisites: the **.NET 10 SDK** and a **PostgreSQL** instance (Docker is easiest).

```bash
# 1. Start Postgres
docker run -d --name tasks-db -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:17

# 2. Restore, build, and run the unit tests
dotnet restore LupiraTasksApi.slnx
dotnet build   LupiraTasksApi.slnx -c Release
dotnet test    tests/LupiraTasksApi.Tests
```

Configure the connection string and (placeholder) OIDC values — the host refuses to start without an
`Authority` and `Audience`, even in development. The JWT authority is only contacted when a real
bearer token is validated, so a placeholder is fine when you authenticate with the dev header:

```bash
export ConnectionStrings__tasks="Host=localhost;Port=5432;Database=lupira_tasks;Username=postgres;Password=postgres"
export Auth__Oidc__Authority="https://idp.example.invalid/"   # placeholder; not contacted by dev-header auth
export Auth__Oidc__Audience="lupira-tasks"
export ASPNETCORE_ENVIRONMENT=Development

dotnet run --project src/LupiraTasksApi
```

In `Development` the schema is created/updated automatically on boot, so the API is ready
immediately. Browse to the Scalar UI at the URL the host prints, then exercise the API with the
development auth header (no identity provider required):

```bash
# Authenticate as any email; add groups to act as an admin.
curl http://localhost:8080/lists -H "X-Dev-User: you@example.com"
curl http://localhost:8080/lists -H "X-Dev-User: you@example.com" -H "X-Dev-Groups: tasks-admins"
```

The same header authenticates the MCP endpoint, so an agent client can be pointed at `/mcp` locally
without an identity provider. Mutations accept an optional `Idempotency-Key` header (a client-minted
GUIDv7) so a redelivered request is a safe no-op that returns the prior result.

## Configuration

All settings bind from configuration or environment variables (the `__` form shown is for
containers). Defaults shown are the code defaults.

| Variable | Required | Purpose |
| --- | --- | --- |
| `ConnectionStrings__tasks` | yes | PostgreSQL connection string (Marten uses schema `tasks`). |
| `Auth__Oidc__Authority` | yes | OIDC issuer/authority URL. |
| `Auth__Oidc__Audience` | yes | Expected token audience. |
| `Auth__AllowedOrigins__0`, `__1`, … | no | CORS allow-list for browser clients. Omit to disable CORS. |
| `RateLimit__RequestsPerMinute` | no (120) | Per-caller token-bucket limit (partitioned by email, else remote IP; disabled on `/dav`). |
| `Share__LinkBaseUrl` | no | Base URL used to build share-link URLs returned to clients. |
| `Ldap__Uri`, `Ldap__BaseDn`, `Ldap__ReaderDn`, `Ldap__ReaderSecret`, `Ldap__Filter` | only for `/dav` | LDAP bind settings for CalDAV HTTP Basic auth. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` (+ standard `OTEL_*`) | no | When set, exports traces, metrics, and logs over OTLP. |
| `ASPNETCORE_ENVIRONMENT` | no | `Development` enables the `X-Dev-User` header and auto-applies the schema. |
| `ASPNETCORE_URLS` | no (`http://0.0.0.0:8080`) | Kestrel bind address (set in the container image). |

## Database schema

Marten owns its objects under the `tasks` schema. In `Development` they are created/updated on boot.
In any other environment auto-create is **off**, so schema changes are a deliberate one-shot run:

```bash
dotnet run --project src/LupiraTasksApi -- --apply-schema
# in a container:  docker run --rm <env...> your-registry/lupira-tasks-api --apply-schema
```

## Deploy

The included [Dockerfile](Dockerfile) is a multi-stage build on the official .NET 10 images; the
runtime layer installs `libldap2` for CalDAV's LDAP bind and exposes port `8080`. Build and run:

```bash
docker build -t lupira-tasks-api .
```

A minimal Compose deployment — every default below is an **overridable sample**, so override each
`${VAR}` for your environment:

```yaml
services:
  tasks-api:
    image: your-registry/lupira-tasks-api:latest
    environment:
      ConnectionStrings__tasks: "Host=postgres;Port=5432;Database=lupira_tasks;Username=lupira_tasks;Password=${DB_PASSWORD:?required}"
      Auth__Oidc__Authority: "${OIDC_AUTHORITY:?required}"          # e.g. https://idp.example.com/application/o/tasks/
      Auth__Oidc__Audience: "${OIDC_AUDIENCE:-lupira-tasks}"
      Share__LinkBaseUrl: "${SHARE_LINK_BASE_URL:-http://localhost:8080}"
      RateLimit__RequestsPerMinute: "${RATE_LIMIT:-120}"
      # OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4318"   # optional: enables OTLP telemetry
    ports:
      - "8080:8080"
    healthcheck:
      test: ["CMD-SHELL", "curl -fsS http://localhost:8080/readyz || exit 1"]
      interval: 60s
      timeout: 5s
      retries: 5
      start_period: 60s
    restart: unless-stopped
```

The repo's [deploy/compose.yaml](deploy/compose.yaml) is the maintained manifest and
[deploy/db/grants.sql](deploy/db/grants.sql) provisions the database role; both ship with default
hostnames that are samples to override.

**Health probes:** `GET /livez` (process up) and `GET /readyz` (process up **and** Postgres
reachable). Apply the schema (above) before the first boot in a non-development environment.

**MCP exposure:** `/mcp` is the most powerful surface (it can act on any of the caller's lists).
Keep it off the public internet — restrict it at your ingress/firewall. As a backstop the app
returns `404` for any `/mcp` request that arrives bearing reverse-proxy edge headers
(`CF-Ray` / `CF-Connecting-IP`).

## CI

- [`.github/workflows/ci.yml`](.github/workflows/ci.yml) — restores and builds the solution on pull
  requests and non-`main` branches.
- [`.github/workflows/release.yml`](.github/workflows/release.yml) — on pushes to `main` and `v*`
  tags, runs CI then builds and pushes a Docker image (tagged `latest`, short SHA, branch, and
  semver).

## License

[MIT](LICENSE) © 2026 Daniel Broström.
