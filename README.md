# LupiraTasksApi

A self-hosted .NET 10 minimal-API backend for Lupira task and command processing. It persists event-worthy state in PostgreSQL via Marten (schema `tasks`), authenticates requests with OIDC bearer tokens, exposes an OpenAPI document at `/openapi/v1.json` with a Scalar UI at `/scalar`, applies a per-caller token-bucket rate limit, and emits OpenTelemetry traces, metrics, and logs when an OTLP endpoint is configured. A liveness probe is available at `/livez` and a readiness probe (pings Postgres) at `/readyz`.

## Project layout

Two projects in one solution (`LupiraTasksApi.slnx`), split so ASP.NET cannot leak into the domain —
see the fleet guide *dotnet-service-architecture* in the DevOps repo's `Guides/`.

- **`src/LupiraTasksApi.Core/`** — the bounded context (`Microsoft.NET.Sdk` class library; `RootNamespace`
  is `LupiraTasksApi`, so namespaces are unchanged across the split). Holds `Domain/` (event-sourced
  aggregates + pure logic), `Application/` (the transport-neutral services + `Caller` + `OpResult`),
  `Dtos/`, `Mappers/`, `Data/` (Marten registrations + the idempotency ledger), and
  `Auth/AccessResolver.cs`. Depends only on Marten — **no ASP.NET reference**.
- **`src/LupiraTasksApi/`** — the thin ASP.NET host (`Microsoft.NET.Sdk.Web`), referencing Core. Holds
  `Program.cs`, the surface adapters (`Endpoints/`+`Handlers/`, `Mcp/`, the `/shared` endpoints),
  HTTP concerns (`Http/`: result mapping, the `Idempotency-Key` reader), and the auth handlers
  (`CurrentUser`, JWT/dev/share-token schemes), plus OTel, rate limiting, OpenAPI, health probes.

## Surfaces

The host exposes **three** surfaces over **one** Marten store — there is no second source of truth.
Each is a thin adapter over the Core `Application` services (`ListService`, `ItemService`,
`SyncService`, `ShareService`):

- **REST `/lists`, `/lists/{id}/items`, `/lists/{id}/sync`, `/lists/{id}/shares`, `/me`, `/users`** — the app surface. Fine-grained CRUD + offline-sync, client-supplied GUIDv7 ids, fractional-index `sortOrder`, `Idempotency-Key`, per-field LWW. This is what the mobile/web clients use.
- **MCP `/mcp`** — the agent surface (Model Context Protocol over Streamable HTTP, `ModelContextProtocol.AspNetCore`). Intent-shaped `[McpServerTool]`s (`list_my_lists`, `create_list`, `find_tasks`, `add_task`, `complete_task`/`reopen_task`, `update_task`, `share_list`, `create_share_link`/`list_share_links`/`revoke_share_link`) that mint ids/sort keys/command-ids server-side, so the agent never deals with the client-sync machinery. The tools call the same services as REST, so an event created via MCP is immediately visible over REST and vice-versa.
- **`/shared/{token}`** — public, account-less share links (read or read/write, optional expiry, revocable). `GET` returns a trimmed, email-free view; a read/write link also exposes the item-mutation routes (reusing the same idempotency + LWW). Writes are attributed `share:{label}`.

### Authentication

- **Production:** OIDC JWT bearer (Authentik) on both surfaces. `NameClaimType = "email"`, `RoleClaimType = "groups"`. The headless agent gets a **member-scoped** token via OAuth2 **token-exchange (RFC 8693)** — it is a confidential Authentik client with an impersonation policy mapping its credential to a target member's `sub`; it presents that `Bearer` token on the MCP transport exactly as a user does on REST, so it inherits exactly that member's own + shared access. No API keys, no `api_keys` table — Authentik is the single identity authority.
- **Development only:** an `X-Dev-User: <email>` header (plus optional `X-Dev-Groups: g1,g2`) authenticates without Authentik, for exercising REST + MCP locally. Registered **only** when `ASPNETCORE_ENVIRONMENT=Development`, so it cannot exist in production (which runs `Production`).

### MCP exposure

`/mcp` is **LAN/WireGuard-only** and is never published through the Cloudflare Tunnel — it is the most powerful surface. The primary control is the tunnel ingress not routing `/mcp*`; as a backstop the app answers any `/mcp` request bearing Cloudflare edge headers (`CF-Ray`/`CF-Connecting-IP`) with a 404. REST `/api` and the health probes remain public.
