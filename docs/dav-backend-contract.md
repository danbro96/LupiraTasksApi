# The `/dav-backend` contract

The internal HTTP seam between the **LupiraDavApi** gateway (the only consumer) and the three domain
backends that hold DAV-projected data:

| Backend | Collection kind | Resource blob | ETag source |
|---|---|---|---|
| lupira-cal-api | `EventCalendar` | `text/calendar` (VEVENT) | `ContentHash` of the canonical ICS |
| lupira-tasks-api | `TodoList` | `text/calendar` (VTODO) | Marten stream `Version` |
| lupira-contact-api | `AddressBook` | `text/vcard` (3.0) | `ContentHash` of the canonical vCard |

Design invariants:

- **Blobs cross the seam, not DTOs.** Serialization, LWW merge, unmodeled-prop splicing, and
  deterministic regeneration are domain logic and stay in the backend.
- **Sync tokens and ctags are opaque strings** minted and parsed only by the owning backend
  (currently the Marten global event sequence). The gateway shuttles them verbatim.
- **ETags are unquoted in JSON bodies**, standard quoted form in HTTP `ETag`/`If-Match` headers.
- **The acting user is the `{email}` path segment** (lowercased login email). The gateway verified the
  human credential (HTTP Basic → LDAP bind) before calling; backends resolve/JIT-provision their own
  principal from it.
- **Auth**: bearer JWT for the backend's own audience, minted by the gateway's client-credentials
  client — the policy additionally requires `azp == DavGateway:ClientId`. The surface is LAN-only
  (not tunneled) with a Cloudflare-header 404 backstop. Development: the `X-Dev-User` header scheme.
- **Idempotency = UID + ETag preconditions**, enforced in the backend service. No command ledger on
  this path. The gateway never retries writes.

## Routes

Base: `/dav-backend/u/{email}` on the backend's own host. All request/response bodies JSON
(camelCase) except resource blobs.

### 1. `GET /collections`

MUST JIT-provision the principal; default-collection bootstrap is backend-discretionary (matches the
old first-PROPFIND self-provision).

```json
{
  "principal": { "displayName": "Anna" },
  "collections": [
    { "id": "3f9c…", "kind": "EventCalendar", "displayName": "Familj",
      "ctag": "seq-4812", "syncToken": "4812" }
  ]
}
```

Also serves single-collection PROPFIND props (the gateway filters); there is no per-collection GET.

### 2. `POST /collections/{id}/query`

Body `{ "uids": [..]|null, "start": ISO8601|null, "end": ISO8601|null, "includeContent": bool }`.
One endpoint covers Depth:1 listing (all null, `includeContent:false`), multiget (`uids` +
`includeContent:true`), and calendar-query time-range (`start`/`end`, half-open UTC window;
recurrence expansion runs in the backend). Backends without time semantics ignore the window.
`uids` + window → intersect.

→ `200 { "resources": [ { "uid", "etag", "content"? } ] }` | `404` (unknown OR inaccessible — opaque).

### 3. `GET /collections/{id}/resources/{uid}`

→ `200` raw blob, `Content-Type: text/calendar|text/vcard; charset=utf-8`, quoted `ETag` | `404`.

### 4. `PUT /collections/{id}/resources/{uid}`

Body = raw blob. `If-Match` / `If-None-Match: *` pass through verbatim.
→ `201`+`ETag` (created) | `204`+`ETag` (updated) | `400` (unparsable) | `403` | `404` | `412`.

### 5. `DELETE /collections/{id}/resources/{uid}`

Optional `If-Match`. → `204` | `403` | `404` | `412`.

### 6. `GET /collections/{id}/changes?since={token}`

→ `200 { "syncToken": "…", "changed": [ { "uid", "etag" } ], "deleted": [ "uid" ] }`.
`since` absent/unknown/unparsable → full listing (`changed` = all live, `deleted` empty) —
self-healing full resync. `deleted` entries are tombstones the gateway renders as 404-status
responses in the sync REPORT.
