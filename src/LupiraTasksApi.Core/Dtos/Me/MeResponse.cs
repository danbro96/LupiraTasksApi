namespace LupiraTasksApi.Dtos.Me;

/// <summary>The caller's provisioned identity, returned by <c>GET /me</c>. <see cref="PrincipalId"/> is
/// the stable internal id a client stores and matches against list members (and uses as its offline
/// actor); email/displayName are for display.</summary>
public sealed class MeResponse
{
    public required Guid PrincipalId { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public required bool IsAdmin { get; set; }
}
