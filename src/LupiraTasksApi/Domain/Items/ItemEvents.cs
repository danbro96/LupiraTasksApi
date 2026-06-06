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
    Guid CommandId);

public record ItemRenamed(Guid ItemId, string Title, DateTimeOffset OccurredAt, Guid CommandId);

public record ItemNotesEdited(Guid ItemId, string? Notes, DateTimeOffset OccurredAt, Guid CommandId);

public record ItemAssigned(Guid ItemId, string? AssigneeEmail, DateTimeOffset OccurredAt, Guid CommandId);

public record ItemDueDateSet(Guid ItemId, DateTimeOffset? DueAt, DateTimeOffset OccurredAt, Guid CommandId);

/// <summary>Commutative add delta — resolved against <see cref="ItemTagRemoved"/> by per-tag (OccurredAt, CommandId).</summary>
public record ItemTagAdded(Guid ItemId, Guid TagId, DateTimeOffset OccurredAt, Guid CommandId);

/// <summary>Commutative remove delta — resolved against <see cref="ItemTagAdded"/> by per-tag (OccurredAt, CommandId).</summary>
public record ItemTagRemoved(Guid ItemId, Guid TagId, DateTimeOffset OccurredAt, Guid CommandId);

public record ItemQuantitySet(Guid ItemId, decimal? Quantity, string? Unit, DateTimeOffset OccurredAt, Guid CommandId);

public record ItemCompleted(Guid ItemId, DateTimeOffset OccurredAt, Guid CommandId);

public record ItemReopened(Guid ItemId, DateTimeOffset OccurredAt, Guid CommandId);

/// <summary>Reparent and/or reorder. SortOrder is a fractional-index string.</summary>
public record ItemMoved(Guid ItemId, Guid? ParentItemId, string SortOrder, DateTimeOffset OccurredAt, Guid CommandId);

/// <summary>Tombstone (stream retained). Once applied, later field events are ignored.</summary>
public record ItemDeleted(Guid ItemId, DateTimeOffset OccurredAt, Guid CommandId);
