namespace LupiraTasksApi.Domain.Lists;

// TodoList event stream (stream id = ListId). Positional records; the first field
// is always the aggregate id. The acting user is carried out-of-band as a Marten
// event-metadata header ("actor"), not as an event field.

public sealed record ListCreated(Guid ListId, string Name, ListKind Kind, string? Color, Guid OwnerPrincipalId);

public sealed record ListRenamed(Guid ListId, string Name);

public sealed record ListRecolored(Guid ListId, string? Color);

/// <summary>Sets whether the list treats priority as a simple on/off (true) or the full 0..9 scale (false).</summary>
public sealed record ListSimplePrioritySet(Guid ListId, bool SimplePriority);

public sealed record ListArchived(Guid ListId);

public sealed record ListRestored(Guid ListId);

/// <summary>Tombstone. Auto-emitted when the last owner leaves the list.</summary>
public sealed record ListDeleted(Guid ListId, string Reason);

// --- Tag definitions (list-scoped) ---

public sealed record TagDefined(Guid ListId, Guid TagId, string Label, string Color);

public sealed record TagRecolored(Guid ListId, Guid TagId, string Color);

public sealed record TagRemoved(Guid ListId, Guid TagId);

public sealed record MemberAdded(Guid ListId, Guid PrincipalId, ListRole Role);

public sealed record MemberRoleChanged(Guid ListId, Guid PrincipalId, ListRole Role);

public sealed record MemberRemoved(Guid ListId, Guid PrincipalId);

/// <summary>One member's own position for this list in their lists screen (a fractional-index key).
/// Per-member: reordering never moves the list for anyone else.</summary>
public sealed record MemberListOrderSet(Guid ListId, Guid PrincipalId, string SortOrder);
