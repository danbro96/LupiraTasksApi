using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Dtos.Items;

namespace LupiraTasksApi.Mappers;

/// <summary>Maps the <see cref="Item"/> snapshot to its response DTO.</summary>
internal static class ItemMapper
{
    public static ItemResponse ToResponse(this Item item) => new()
    {
        Id = item.Id,
        Version = item.Version,
        ListId = item.ListId,
        ParentItemId = item.ParentItemId,
        Title = item.Title,
        Notes = item.Notes,
        Status = item.Status,
        StatusReason = item.StatusReason,
        Completed = item.Completed,
        CompletedAt = item.CompletedAt,
        CompletedBy = item.CompletedBy,
        AssignedTo = item.AssignedTo,
        DueAt = item.DueAt,
        Quantity = item.Quantity,
        Unit = item.Unit,
        Priority = item.Priority,
        Tags = item.Tags.ToList(),
        SortOrder = item.SortOrder,
        CreatedBy = item.CreatedBy,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
    };
}
