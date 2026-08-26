namespace LupiraTasksApi.Core.Dtos.Shared;

/// <summary>A tag definition as shown on a shared list (no sensitive data).</summary>
public sealed class SharedTagResponse
{
    public required Guid Id { get; set; }

    public required string Label { get; set; }

    public required string Color { get; set; }
}
