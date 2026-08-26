namespace LupiraTasksApi.Core.Dtos.Users;

/// <summary>The distinct people across the caller's lists, for member-add autocomplete.</summary>
public sealed class DirectoryResponse
{
    public required IReadOnlyList<DirectoryPerson> People { get; set; }
}
