using LupiraTasksApi.Domain;

namespace LupiraTasksApi.Dtos.Lists;

/// <summary>
/// Add a member to a list by email. Any member may add someone; the role defaults to
/// <see cref="ListRole.Editor"/> when omitted. Only a real Authentik user can ever use the
/// membership (provisioned on first login), so a wrong email is simply inert.
/// </summary>
public sealed class AddMemberRequest
{
    public required string Email { get; set; }

    /// <summary>Role to grant. Defaults to Editor when null.</summary>
    public ListRole? Role { get; set; }
}
