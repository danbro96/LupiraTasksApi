# LupiraTasksApi

A self-hosted .NET 10 minimal-API backend for Lupira task and command processing. It persists event-worthy state in PostgreSQL via Marten (schema `tasks`), authenticates requests with OIDC bearer tokens, exposes an OpenAPI document at `/openapi/v1.json` with a Scalar UI at `/scalar`, applies a per-caller token-bucket rate limit, and emits OpenTelemetry traces, metrics, and logs when an OTLP endpoint is configured. A liveness probe is available at `/livez` and a readiness probe (pings Postgres) at `/readyz`.
