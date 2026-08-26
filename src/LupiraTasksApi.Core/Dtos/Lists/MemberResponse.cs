using LupiraTasksApi.Core.Domain;

namespace LupiraTasksApi.Core.Dtos.Lists;

/// <summary>A member of a list: the stable <c>PrincipalId</c> plus resolved <c>Email</c>/<c>DisplayName</c>.</summary>
public sealed class MemberResponse
{
    public required Guid PrincipalId { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public required ListRole Role { get; set; }
    public required DateTimeOffset AddedAt { get; set; }
    /// <summary>Who added them; <c>null</c> for a share-link add or an unresolved actor.</summary>
    public PersonRef? AddedBy { get; set; }
}
