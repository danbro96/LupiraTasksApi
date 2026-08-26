namespace LupiraTasksApi.Core.Domain.Items.Events;

/// <summary>Reparent and/or reorder. SortOrder is a fractional-index string.</summary>
public sealed record ItemMoved(Guid ItemId, Guid? ParentItemId, string SortOrder, DateTimeOffset OccurredAt, Guid CommandId);
