using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Dtos.Lists;

namespace LupiraTasksApi.Mappers;

/// <summary>Maps the <see cref="TodoList"/> snapshot to its response DTO.</summary>
internal static class ListMapper
{
    public static ListResponse ToResponse(this TodoList list) => new()
    {
        Id = list.Id,
        Version = list.Version,
        Name = list.Name,
        Kind = list.Kind,
        Color = list.Color,
        SimplePriority = list.SimplePriority,
        OwnerEmail = list.OwnerEmail,
        IsArchived = list.IsArchived,
        CreatedAt = list.CreatedAt,
        UpdatedAt = list.UpdatedAt,
        Tags = list.Tags
            .Select(t => new TagResponse { Id = t.Id, Label = t.Label, Color = t.Color })
            .ToList(),
        Members = list.Members
            .Select(m => new MemberResponse
            {
                Email = m.Email,
                Role = m.Role,
                AddedAt = m.AddedAt,
                AddedBy = m.AddedBy,
            })
            .ToList(),
    };
}
