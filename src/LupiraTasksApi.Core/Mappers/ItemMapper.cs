using System.Text.Json.Nodes;
using LupiraTasksApi.Core.Domain.Identity;
using LupiraTasksApi.Core.Domain.Items;
using LupiraTasksApi.Core.Dtos;
using LupiraTasksApi.Core.Dtos.Items;

namespace LupiraTasksApi.Core.Mappers;

/// <summary>Maps the <see cref="Item"/> snapshot to its response DTO, resolving assignee + attribution
/// principal ids to <see cref="PersonRef"/> via a lookup built by the calling service.</summary>
internal static class ItemMapper
{
    public static ItemDto ToResponse(this Item item, IReadOnlyDictionary<Guid, Principal> principals) => new()
    {
        Id = item.Id,
        ListId = item.ListId,
        ParentItemId = item.ParentItemId,
        Title = item.Title,
        Notes = item.Notes,
        Status = item.Status,
        StatusReason = item.StatusReason,
        Completed = item.Completed,
        CompletedAt = item.CompletedAt,
        CompletedBy = PersonRef.FromActor(item.CompletedBy, principals),
        Assignee = item.AssignedToPrincipalId is { } a ? PersonRef.From(a, principals) : null,
        DueAt = item.DueAt,
        Quantity = item.Quantity,
        Unit = item.Unit,
        Priority = item.Priority,
        Tags = item.Tags.ToList(),
        SortOrder = item.SortOrder,
        CreatedBy = PersonRef.FromActor(item.CreatedBy, principals),
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        Metadata = string.IsNullOrWhiteSpace(item.Metadata) ? null : JsonNode.Parse(item.Metadata)?.AsObject(),
    };
}
