namespace LupiraTasksApi.Core.Domain.Items.Events;

public sealed record ItemAdded(
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
