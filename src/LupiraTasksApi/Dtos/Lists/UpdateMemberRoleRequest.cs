using LupiraTasksApi.Domain;

namespace LupiraTasksApi.Dtos.Lists;

/// <summary>Change a member's role on a list (Owner only).</summary>
public sealed class UpdateMemberRoleRequest
{
    public required ListRole Role { get; set; }
}
