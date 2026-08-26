using LupiraTasksApi.Core.Domain;
using LupiraTasksApi.Core.Domain.Items;
using LupiraTasksApi.Core.Domain.Lists;
using LupiraTasksApi.Core.Dtos.Items;
using LupiraTasksApi.Core.Dtos.Shared;

namespace LupiraTasksApi.Core.Mappers;

/// <summary>
/// Maps domain snapshots to the TRIMMED public share DTOs — the single place that decides what a
/// share-link recipient may see. Every email field (owner, members, assignee, creator, completer)
/// is intentionally dropped here.
/// </summary>
public static class SharedMapper
{
    public static SharedItemDto ToShared(this Item item) => new()
    {
        Id = item.Id,
        ParentItemId = item.ParentItemId,
        Title = item.Title,
        Notes = item.Notes,
        Completed = item.Completed,
        CompletedAt = item.CompletedAt,
        DueAt = item.DueAt,
        Quantity = item.Quantity,
        Unit = item.Unit,
        Priority = item.Priority,
        Tags = item.Tags.ToList(),
        SortOrder = item.SortOrder,
    };

    /// <summary>Trim a full <see cref="ItemDto"/> (returned by the reused ItemService) to the public shape.</summary>
    public static SharedItemDto ToShared(this ItemDto item) => new()
    {
        Id = item.Id,
        ParentItemId = item.ParentItemId,
        Title = item.Title,
        Notes = item.Notes,
        Completed = item.Completed,
        CompletedAt = item.CompletedAt,
        DueAt = item.DueAt,
        Quantity = item.Quantity,
        Unit = item.Unit,
        Priority = item.Priority,
        Tags = item.Tags.ToList(),
        SortOrder = item.SortOrder,
    };

    public static SharedListResponse ToShared(this TodoList list, ShareAccess access, IReadOnlyList<SharedItemDto> items) => new()
    {
        Name = list.Name,
        Kind = list.Kind,
        Color = list.Color,
        SimplePriority = list.SimplePriority,
        Access = access,
        Tags = list.Tags.Select(t => new SharedTagDto { Id = t.Id, Label = t.Label, Color = t.Color }).ToList(),
        Items = items,
    };
}
