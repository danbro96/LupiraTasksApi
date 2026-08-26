using LupiraTasksApi.Core.Domain;
using LupiraTasksApi.Core.Domain.Identity;
using LupiraTasksApi.Core.Domain.Lists;
using LupiraTasksApi.Core.Dtos;
using LupiraTasksApi.Core.Dtos.Lists;

namespace LupiraTasksApi.Core.Mappers;

/// <summary>Maps the <see cref="TodoList"/> snapshot to its response DTO, resolving owner + member
/// principal ids to <see cref="PersonRef"/> via a lookup built by the calling service.
/// <paramref name="callerPrincipalId"/> selects the caller's own membership row for <c>Access</c>
/// and <c>SortOrder</c>.</summary>
internal static class ListMapper
{
    public static ListDto ToResponse(this TodoList list, IReadOnlyDictionary<Guid, Principal> principals, Guid callerPrincipalId) => new()
    {
        Id = list.Id,
        Name = list.Name,
        Kind = list.Kind,
        Color = list.Color,
        SimplePriority = list.SimplePriority,
        Owner = PersonRef.From(list.OwnerPrincipalId, principals)
            ?? new PersonRef { PrincipalId = list.OwnerPrincipalId, Email = string.Empty },
        Access = list.Members.Find(m => m.PrincipalId == callerPrincipalId)?.Role ?? ListRole.Viewer,
        // The caller's own screen position — never another member's (MemberDto omits it).
        SortOrder = list.Members.Find(m => m.PrincipalId == callerPrincipalId)?.SortOrder,
        IsArchived = list.IsArchived,
        ArchivedAt = list.ArchivedAt,
        CreatedAt = list.CreatedAt,
        UpdatedAt = list.UpdatedAt,
        Tags = list.Tags
            .Select(t => new TagDto { Id = t.Id, Label = t.Label, Color = t.Color })
            .ToList(),
        Members = list.Members
            .Select(m => new MemberDto
            {
                PrincipalId = m.PrincipalId,
                Email = principals.TryGetValue(m.PrincipalId, out var p) ? p.Email : string.Empty,
                DisplayName = principals.TryGetValue(m.PrincipalId, out var d) ? d.DisplayName : null,
                Role = m.Role,
                AddedAt = m.AddedAt,
                AddedBy = PersonRef.FromActor(m.AddedBy, principals),
            })
            .ToList(),
    };
}
