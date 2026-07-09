# Tracking backbone — the assistant's tracked-to-done backlog

**Status:** design. The API is deployed; proposed changes here are **additive and backward-compatible** (new events/fields/documents), not rewrites.
**Primacy:** REST + the `tasks` Marten store are primary. CalDAV/CardDAV VTODO is secondary — and the model already treats structured fields as canonical (VTODO is regenerated from the live snapshot; `SourceVtodo` only preserves unmodeled props), so no iCal-as-source-of-truth cleanup is needed here.

## Purpose

LupiraTasksApi is the assistant's (and the user's) **backlog** — the substrate for work that is **tracked to completion and has no firing moment**. It is the complement of LupiraCalApi:

- **cal-api** = things that **fire** (events, prompts that run at a time). Owns the clock.
- **tasks-api** = things **tracked to done** (open items with a lifecycle, no firing moment). Owns the backlog.

The decisive line is *fires vs tracked-to-done*, not *time-bound vs not* — tasks carry a `DueAt`, but tasks-api never fires on it. Anything that must happen at a time is a cal-api Prompt; tasks-api just tracks open work until it's closed.

Examples (assistant-driven): an unhealthy API → a **task** (fix it, tracked until closed); "notify me when the game releases" → a **task** (the durable goal) whose checking heartbeat is a cal-api Prompt; "research desserts Friday and report" → a cal-api **Prompt event**, *not* a task.

**Bills and deliveries are tasks** (tracked to done — paid / received), never calendar events — assistant-api routes each to a dedicated, user-owned list it scaffolds on first use (**"Bills"**, **"Deliveries"**). The list is the classifier; the structured fields ride the item's `Metadata` blob (bill: `{ kind:"bill", amount, currency, payee, invoiceNumber }`; delivery: `{ kind:"delivery", carrier, trackingNumber, trackingUrl, orderReference }`). `DueAt` holds the payment due date / expected arrival — passive here; the timed nudge is a **linked cal-api Prompt** (`Relation(ToKind="cal-item", RelationType="monitors")`), exactly the standing-monitor shape below.

## What already supports it

| Need | Already in the code |
|---|---|
| Lists with roles | `TodoList` + `Member` (Owner/Editor/Viewer), `OwnerEmail` (`Domain/Lists/TodoList.cs`) |
| **Agent can own lists** | any email owns lists via `OwnerEmail`; no domain blocker — an agent principal owns its own lists and adds humans as members |
| Items tracked to done | `Item` — `Completed` + `CompletedAt/By`, `Reopened* `, soft `Deleted` (`Domain/Items/Item.cs`, `ItemState.cs`) |
| Due / assignee / priority / tags / subtasks | `DueAt`, `AssignedTo`, `Priority` (0..9), `Tags`, `ParentItemId` |
| Agent surface | 16 MCP tools — lists, tasks (incl. `set_task_status`/`set_task_metadata`), relations, sharing (`Mcp/TaskTools.cs`), bearer JWT on-behalf-of (device-code + refresh) |
| Offline-safe writes | per-field LWW `(OccurredAt, CommandId)` + idempotency ledger, single `SaveChangesAsync` (`Application/Idempotency`, `Domain/Items/ItemLww.cs`) |
| Event sourcing | Marten inline snapshots, schema `tasks`, Sequence as sync-token (`Data/MartenRegistrations.cs`) |

So "the assistant gets its own lists and drives them" needs **no new model** — the MCP surface and ownership already cover it.

## Non-goal — no scheduler in tasks-api

tasks-api must **not** grow due-date firing, reminders, recurrence expansion, a daemon, or an outbox. That is exactly what cal-api's `scheduled_fire` engine is for. Keeping firing out of tasks-api is what makes the substrate split clean: any timed behaviour on a task is a **linked cal-api Prompt**, not a tasks-api feature. The existing `DueAt` stays a passive sort/display field.

## The additions (all shipped)

```mermaid
classDiagram
  class TodoList {
    ListKind Kind
    string OwnerEmail
    Member[] Members
  }
  class Item {
    string Title
    bool Completed
    DateTimeOffset DueAt
    int Priority
    ItemStatus Status
    string Metadata
  }
  class Relation {
    string FromKind
    Guid FromId
    string ToKind
    string ToRef
    string RelationType
  }
  class CalPrompt {
    <<cal-api>>
  }
  class ExternalRef {
    <<github / incident / PR / url>>
  }
  TodoList "1" o-- "*" Item : contains
  Item "1" --> "*" Relation : links
  Relation ..> CalPrompt : monitor heartbeat
  Relation ..> ExternalRef : source / output
```

### Keystone: Relations / cross-links *(shipped — `Domain/Relation.cs`, `Application/RelationService.cs`)*
Mirror cal-api's `Relation` exactly, so the two services share one linking vocabulary:
```
Relation { FromKind="task"; FromId; ToKind; ToRef; RelationType; Metadata? }   // plain Marten doc, indexed by FromId
```
REST: `POST/GET/DELETE /lists/{listId}/items/{itemId}/relations`.
This is the keystone because it wires tasks into the assistant graph:
- **`ToKind="cal-item"`** → the cal-api Prompt that is this monitor's checking heartbeat.
- **`ToKind="url"`** → the GitHub issue/PR, the health-incident, the release page being watched.
- **`RelationType`** → `monitors`, `spawned-by`, `produced`, `blocked-by`, `relates-to`.

Without this, a task can't point at its heartbeat prompt or its source, and the standing-monitor pattern can't be expressed.

### Agent-owned list designation *(shipped — `ListKind.Agent`)*
`ListKind.Agent` (alongside `Todo`, `Shopping`) distinguishes agent/system lists from the user's own in queries and UI. Functionally lists already worked via `OwnerEmail` + membership; this is just the label. Scoping:
- **Per-user agent work** (research, follow-ups for user X) → a designated agent list in X's account, isolated per the platform's LLM/data-isolation rules.
- **System/ops work** ("API unhealthy", package upgrades) → an operator-owned list — the tasks counterpart of the **DevOps** calendar; not any end-user's.

### Richer item status *(shipped — `Domain/Enums.cs`)*
An `ItemStatus` enum + reason so the assistant can represent stuck work, not just done/undone:
```
enum ItemStatus { Open, InProgress, Blocked, Waiting, Done, Cancelled }
event ItemStatusChanged(Guid ItemId, ItemStatus Status, string? Reason, DateTimeOffset OccurredAt, Guid CommandId)
```
One LWW-guarded field; `Completed` is now derived (`Status == Done`). Lets the assistant answer "what's blocked / waiting on me?".

### Structured metadata on items *(shipped — `ItemMetadataSet`, whole-field LWW)*
A JSONB `Metadata` field (mirroring cal-api `Item.Metadata`) for agent bookkeeping that doesn't deserve a typed column — source-alert id, check count, last-result summary. Server-side only; never in VTODO or share links.

## The standing-monitor pattern (grounded)
"Keep checking until X" composes the two substrates rather than adding a scheduler here:
1. **Task** (tasks-api) — the durable goal, `Open` until met. The thing tracked.
2. **Recurring Prompt** (cal-api) — the checking heartbeat that fires on a schedule (cal-api owns firing).
3. **Link** — the task's `Relation(ToKind="cal-item", RelationType="monitors")` ↔ the prompt.
4. On a successful check the assistant **completes the task**, notifies, and **cancels the recurring prompt**.

## Consent & isolation
- **Agent self-tracking is internal bookkeeping** — creating its own task/list needs no user approval. The real-world action it leads to (the PR, writing the user's data, notifying) still follows consent-first.
- **Isolation** follows the platform rule: per-user agent lists are the user's and never cross users in an LLM call; system/ops lists are the operator's.

## Decisions
1. ✅ Relations = a plain `Relation` doc (mirrors cal-api), deterministic tuple-derived id for idempotent link/unlink.
2. ✅ Agent-list designation = `ListKind.Agent`.
3. ✅ Richer `ItemStatus` shipped with the rest.
4. **Open:** completing a monitor task does NOT auto-cancel its linked recurring prompt — the assistant does both explicitly. Revisit if orphaned heartbeats become a problem.
