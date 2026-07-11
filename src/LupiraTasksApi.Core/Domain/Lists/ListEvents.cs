namespace LupiraTasksApi.Domain.Lists;

// TodoList event stream (stream id = ListId). Positional records; the first field
// is always the aggregate id. The acting user is carried out-of-band as a Marten
// event-metadata header ("actor"), not as an event field.

// --- Lifecycle ---

public record ListCreated(Guid ListId, string Name, ListKind Kind, string? Color, Guid OwnerPrincipalId);

public record ListRenamed(Guid ListId, string Name);

public record ListRecolored(Guid ListId, string? Color);

/// <summary>Sets whether the list treats priority as a simple on/off (true) or the full 0..9 scale (false).</summary>
public record ListSimplePrioritySet(Guid ListId, bool SimplePriority);

public record ListArchived(Guid ListId);

public record ListRestored(Guid ListId);

/// <summary>Tombstone. Auto-emitted when the last owner leaves the list.</summary>
public record ListDeleted(Guid ListId, string Reason);

// --- Tag definitions (list-scoped) ---

public record TagDefined(Guid ListId, Guid TagId, string Label, string Color);

public record TagRecolored(Guid ListId, Guid TagId, string Color);

public record TagRemoved(Guid ListId, Guid TagId);

// --- Membership ---

public record MemberAdded(Guid ListId, Guid PrincipalId, ListRole Role);

public record MemberRoleChanged(Guid ListId, Guid PrincipalId, ListRole Role);

public record MemberRemoved(Guid ListId, Guid PrincipalId);
