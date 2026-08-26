using LupiraTasksApi.Core.Domain;

namespace LupiraTasksApi.Core.Ical;

/// <summary>The modeled fields lifted out of an inbound VTODO. Everything else (RRULE, X-*…)
/// is preserved opaquely in the raw blob and re-emitted by <see cref="VtodoMapper.ToVtodo"/>.</summary>
public readonly record struct VtodoFields(
    string Title,
    string? Notes,
    DateTimeOffset? DueAt,
    ItemStatus Status,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<string> Categories,
    int Priority);
