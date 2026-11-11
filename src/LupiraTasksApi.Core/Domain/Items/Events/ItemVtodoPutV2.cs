namespace LupiraTasksApi.Core.Domain.Items.Events;

/// <summary>
/// A whole-VTODO write from the CalDAV surface (a DAVx5 PUT). Carries the parsed modeled
/// fields plus the raw VTODO blob (<see cref="SourceVtodo"/>) for lossless round-trip of
/// properties this model doesn't represent. Competes per-field through the same per-field LWW
/// guards as the granular REST/MCP events, keyed on (OccurredAt, CommandId) — so a DAV PUT and
/// a concurrent REST edit converge field-by-field. When it is the first event on a stream it
/// also establishes ListId / Uid / CreatedAt (DAV-created item).
/// </summary>
public sealed record ItemVtodoPutV2(
    Guid ItemId,
    Guid ListId,
    string Uid,
    string Title,
    string? Notes,
    DateOnly? DueOn,
    DateTimeOffset? DueAt,
    ItemStatus Status,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<Guid> Tags,
    string SortOrder,
    string SourceVtodo,
    DateTimeOffset OccurredAt,
    Guid CommandId,
    int Priority);
