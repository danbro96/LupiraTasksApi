namespace LupiraTasksApi.Dtos.Items;

/// <summary>
/// Reparent and/or reorder an item. <see cref="SortOrder"/> is a fractional-index
/// string so concurrent inserts get distinct keys without renumbering siblings.
/// </summary>
public sealed class MoveItemRequest
{
    public Guid? ParentItemId { get; set; }
    public required string SortOrder { get; set; }

    /// <summary>Client wall-clock at the moment of the change (LWW key). Defaults to server now.</summary>
    public DateTimeOffset? OccurredAt { get; set; }
}

/// <summary>
/// Body for complete/reopen/delete. Carries only the LWW timestamp; the item id is in
/// the route. <see cref="OccurredAt"/> defaults to server now when omitted.
/// </summary>
public sealed class ItemTimestampRequest
{
    /// <summary>Client wall-clock at the moment of the change (LWW key). Defaults to server now.</summary>
    public DateTimeOffset? OccurredAt { get; set; }
}
