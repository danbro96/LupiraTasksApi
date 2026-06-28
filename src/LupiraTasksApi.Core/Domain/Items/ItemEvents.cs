using LupiraTasksApi.Domain;

namespace LupiraTasksApi.Domain.Items;

// Item event stream (stream id = ItemId). Positional records; the first field is
// always the aggregate id. Every event carries OccurredAt — the client wall-clock
// at the moment the change was made — which is the primary key for per-field
// last-writer-wins (LWW) conflict resolution when offline edits sync out of order.
// Every event also carries CommandId (the originating command's id = the
// Idempotency-Key, or a server-minted GUIDv7 when none was sent): it is the
// deterministic tiebreaker when two concurrent edits to the same field share an
// identical OccurredAt, so the server and the offline client reducer converge on
// the same winner regardless of apply order. The acting user is carried out-of-band
// as a Marten event-metadata header ("actor").

public record ItemAdded(
    Guid ItemId,
    Guid ListId,
    Guid? ParentItemId,
    string Title,
    string SortOrder,
    DateTimeOffset OccurredAt,
    Guid CommandId,
    // The CalDAV resource UID. Null on the REST/MCP/share surfaces (defaults to the item id);
    // a client-supplied VTODO UID when the item is created over CalDAV. Trailing-optional so the
    // existing positional call sites are unchanged.
    string? Uid = null);

public record ItemRenamed(Guid ItemId, string Title, DateTimeOffset OccurredAt, Guid CommandId);

public record ItemNotesEdited(Guid ItemId, string? Notes, DateTimeOffset OccurredAt, Guid CommandId);

public record ItemAssigned(Guid ItemId, string? AssigneeEmail, DateTimeOffset OccurredAt, Guid CommandId);

public record ItemDueDateSet(Guid ItemId, DateTimeOffset? DueAt, DateTimeOffset OccurredAt, Guid CommandId);

/// <summary>Commutative add delta — resolved against <see cref="ItemTagRemoved"/> by per-tag (OccurredAt, CommandId).</summary>
public record ItemTagAdded(Guid ItemId, Guid TagId, DateTimeOffset OccurredAt, Guid CommandId);

/// <summary>Commutative remove delta — resolved against <see cref="ItemTagAdded"/> by per-tag (OccurredAt, CommandId).</summary>
public record ItemTagRemoved(Guid ItemId, Guid TagId, DateTimeOffset OccurredAt, Guid CommandId);

public record ItemQuantitySet(Guid ItemId, decimal? Quantity, string? Unit, DateTimeOffset OccurredAt, Guid CommandId);

/// <summary>Sets the standard iCalendar priority (0 = none, 1..9 in range).</summary>
public record ItemPrioritySet(Guid ItemId, int Priority, DateTimeOffset OccurredAt, Guid CommandId);

public record ItemCompleted(Guid ItemId, DateTimeOffset OccurredAt, Guid CommandId);

public record ItemReopened(Guid ItemId, DateTimeOffset OccurredAt, Guid CommandId);

/// <summary>
/// Sets the item's lifecycle <see cref="ItemStatus"/> (and optional reason). The general lifecycle setter:
/// <see cref="ItemCompleted"/>/<see cref="ItemReopened"/> are intent-revealing shorthands that project onto the
/// same single status guard, so a status change and a complete/reopen converge as one field by (OccurredAt, CommandId).
/// </summary>
public record ItemStatusChanged(Guid ItemId, ItemStatus Status, string? Reason, DateTimeOffset OccurredAt, Guid CommandId);

/// <summary>Reparent and/or reorder. SortOrder is a fractional-index string.</summary>
public record ItemMoved(Guid ItemId, Guid? ParentItemId, string SortOrder, DateTimeOffset OccurredAt, Guid CommandId);

/// <summary>Sets the whole <see cref="ItemState.Metadata"/> JSON blob (server-side bookkeeping). Whole-field LWW.</summary>
public record ItemMetadataSet(Guid ItemId, string? Metadata, DateTimeOffset OccurredAt, Guid CommandId);

/// <summary>Tombstone (stream retained). Once applied, later field events are ignored.</summary>
public record ItemDeleted(Guid ItemId, DateTimeOffset OccurredAt, Guid CommandId);

/// <summary>
/// A whole-VTODO write from the CalDAV surface (a DAVx5 PUT). Carries the parsed modeled
/// fields plus the raw VTODO blob (<see cref="SourceVtodo"/>) for lossless round-trip of
/// properties this model doesn't represent. Competes per-field through the same per-field LWW
/// guards as the granular REST/MCP events, keyed on (OccurredAt, CommandId) — so a DAV PUT and
/// a concurrent REST edit converge field-by-field. When it is the first event on a stream it
/// also establishes ListId / Uid / CreatedAt (DAV-created item).
/// </summary>
public record ItemVtodoPut(
    Guid ItemId,
    Guid ListId,
    string Uid,
    string Title,
    string? Notes,
    DateTimeOffset? DueAt,
    ItemStatus Status,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<Guid> Tags,
    string SortOrder,
    string SourceVtodo,
    DateTimeOffset OccurredAt,
    Guid CommandId,
    // The standard VTODO PRIORITY (0 = none, 1..9). Trailing-optional so existing positional call
    // sites are unchanged and historical persisted events (without the field) still deserialize to 0.
    int Priority = 0);
