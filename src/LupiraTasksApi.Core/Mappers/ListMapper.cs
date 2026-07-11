using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Identity;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Dtos;
using LupiraTasksApi.Dtos.Lists;

namespace LupiraTasksApi.Mappers;

/// <summary>Maps the <see cref="TodoList"/> snapshot to its response DTO, resolving owner + member
/// principal ids to <see cref="PersonRef"/> via a lookup built by the calling service.
/// <paramref name="callerPrincipalId"/> selects the caller's own membership role for <c>Access</c>.</summary>
internal static class ListMapper
{
    public static ListResponse ToResponse(this TodoList list, IReadOnlyDictionary<Guid, Principal> principals, Guid callerPrincipalId) => new()
    {
        Id = list.Id,
        Version = list.Version,
        Name = list.Name,
        Kind = list.Kind,
        Color = list.Color,
        SimplePriority = list.SimplePriority,
        Owner = PersonRef.From(list.OwnerPrincipalId, principals)
            ?? new PersonRef { PrincipalId = list.OwnerPrincipalId, Email = "" },
        Access = list.Members.Find(m => m.PrincipalId == callerPrincipalId)?.Role ?? ListRole.Viewer,
        IsArchived = list.IsArchived,
        CreatedAt = list.CreatedAt,
        UpdatedAt = list.UpdatedAt,
        Tags = list.Tags
            .Select(t => new TagResponse { Id = t.Id, Label = t.Label, Color = t.Color })
            .ToList(),
        Members = list.Members
            .Select(m => new MemberResponse
            {
                PrincipalId = m.PrincipalId,
                Email = principals.TryGetValue(m.PrincipalId, out var p) ? p.Email : "",
                DisplayName = principals.TryGetValue(m.PrincipalId, out var d) ? d.DisplayName : null,
                Role = m.Role,
                AddedAt = m.AddedAt,
                AddedBy = PersonRef.FromActor(m.AddedBy, principals),
            })
            .ToList(),
    };
}
