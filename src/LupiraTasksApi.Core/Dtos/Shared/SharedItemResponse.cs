namespace LupiraTasksApi.Dtos.Shared;

/// <summary>
/// An item as shown to a share-link recipient. Mirrors the fields a viewer/editor needs but
/// OMITS every email field (<c>assignedTo</c>, <c>completedBy</c>, <c>createdBy</c>) so a public
/// link never leaks family emails.
/// </summary>
public sealed class SharedItemResponse
{
    public required Guid Id { get; set; }
    public Guid? ParentItemId { get; set; }
    public required string Title { get; set; }
    public string? Notes { get; set; }
    public required bool Completed { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public required IReadOnlyList<Guid> Tags { get; set; }
    public required string SortOrder { get; set; }
}
