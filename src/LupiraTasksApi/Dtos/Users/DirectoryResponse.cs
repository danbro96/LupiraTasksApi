namespace LupiraTasksApi.Dtos.Users;

/// <summary>A person the caller has seen across their shared lists (for adding members).</summary>
public sealed class DirectoryPerson
{
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>The distinct people across the caller's lists, for member-add autocomplete.</summary>
public sealed class DirectoryResponse
{
    public required IReadOnlyList<DirectoryPerson> People { get; set; }
}
