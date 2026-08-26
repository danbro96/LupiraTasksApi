namespace LupiraTasksApi.Core.Domain.Lists;

/// <summary>A tag definition scoped to a single list.</summary>
public sealed class TagDef
{
    public Guid Id { get; set; }

    public string Label { get; set; } = string.Empty;

    public string Color { get; set; } = string.Empty;
}
