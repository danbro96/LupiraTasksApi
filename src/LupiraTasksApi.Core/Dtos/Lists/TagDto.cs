namespace LupiraTasksApi.Core.Dtos.Lists;

/// <summary>A tag definition on a list.</summary>
public sealed class TagDto
{
    public required Guid Id { get; set; }

    public required string Label { get; set; }

    public required string Color { get; set; }
}
