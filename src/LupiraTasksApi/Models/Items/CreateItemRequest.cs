namespace LupiraTasksApi.Models.Items;

/// <summary>
/// Add an item. The client supplies a GUIDv7 <see cref="Id"/> (idempotent create) and a
/// fractional-index <see cref="SortOrder"/> string. <see cref="OccurredAt"/> is the
/// client wall-clock used for last-writer-wins; when omitted the server stamps now.
/// </summary>
public sealed class CreateItemRequest
{
    public required Guid Id { get; set; }
    public required string Title { get; set; }
    public Guid? ParentItemId { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public string? AssigneeEmail { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public IReadOnlyList<Guid>? TagIds { get; set; }
    public required string SortOrder { get; set; }

    /// <summary>Client wall-clock at the moment of the change (LWW key). Defaults to server now.</summary>
    public DateTimeOffset? OccurredAt { get; set; }
}
