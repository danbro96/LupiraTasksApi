namespace LupiraTasksApi.Dtos.Me;

/// <summary>The caller's provisioned identity, returned by <c>GET</c>/<c>PATCH /me</c>.</summary>
public sealed class MeResponse
{
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public required bool IsAdmin { get; set; }
}
