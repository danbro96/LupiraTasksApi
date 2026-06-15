using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Dtos.Items;
using LupiraTasksApi.Dtos.Shared;

namespace LupiraTasksApi.Mappers;

/// <summary>
/// Maps domain snapshots to the TRIMMED public share DTOs — the single place that decides what a
/// share-link recipient may see. Every email field (owner, members, assignee, creator, completer)
/// is intentionally dropped here.
/// </summary>
internal static class SharedMapper
{
    public static SharedItemResponse ToShared(this Item item) => new()
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
        Tags = item.Tags.ToList(),
        SortOrder = item.SortOrder,
    };

    /// <summary>Trim a full <see cref="ItemResponse"/> (returned by the reused ItemService) to the public shape.</summary>
    public static SharedItemResponse ToShared(this ItemResponse item) => new()
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
        Tags = item.Tags.ToList(),
        SortOrder = item.SortOrder,
    };

    public static SharedListResponse ToShared(this TodoList list, ShareAccess access, IReadOnlyList<SharedItemResponse> items) => new()
    {
        Name = list.Name,
        Kind = list.Kind,
        Color = list.Color,
        Access = access,
        Tags = list.Tags.Select(t => new SharedTagResponse { Id = t.Id, Label = t.Label, Color = t.Color }).ToList(),
        Items = items,
    };
}
