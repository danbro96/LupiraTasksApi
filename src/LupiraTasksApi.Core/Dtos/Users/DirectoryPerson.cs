namespace LupiraTasksApi.Core.Dtos.Users;

/// <summary>A person the caller has seen across their shared lists (for adding members).</summary>
public sealed class DirectoryPerson
{
    public required Guid PrincipalId { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
}
