namespace LupiraTasksApi.Dtos.Items;

/// <summary>An item's current snapshot. Clients nest by <see cref="ParentItemId"/>.</summary>
public sealed class ItemResponse
{
    public required Guid Id { get; set; }
    public required int Version { get; set; }
    public required Guid ListId { get; set; }
    public Guid? ParentItemId { get; set; }
    public required string Title { get; set; }
    public string? Notes { get; set; }
    public required bool Completed { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
    public string? AssignedTo { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public required int Priority { get; set; }
    public required IReadOnlyList<Guid> Tags { get; set; }
    public required string SortOrder { get; set; }
    public string? CreatedBy { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Envelope for a list's items.</summary>
public sealed class ItemCollectionResponse
{
    public required IReadOnlyList<ItemResponse> Items { get; set; }
}
