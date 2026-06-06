using LupiraTasksApi.Domain;

namespace LupiraTasksApi.Models.Lists;

/// <summary>
/// Create a list. The client supplies a GUIDv7 <see cref="Id"/> so the create is
/// idempotent on replay (an existing stream is treated as success). The caller
/// becomes the Owner.
/// </summary>
public sealed class CreateListRequest
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required ListKind Kind { get; set; }
    public string? Color { get; set; }
}
