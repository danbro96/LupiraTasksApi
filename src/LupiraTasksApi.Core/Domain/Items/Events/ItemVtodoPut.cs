namespace LupiraTasksApi.Core.Domain.Items.Events;

/// <summary>Schema v1 of <see cref="ItemVtodoPutV2"/>, read through its upcaster.</summary>
public sealed record ItemVtodoPut(
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
