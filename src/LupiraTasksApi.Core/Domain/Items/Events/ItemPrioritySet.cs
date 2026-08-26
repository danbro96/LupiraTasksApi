namespace LupiraTasksApi.Core.Domain.Items.Events;

/// <summary>Sets the standard iCalendar priority (0 = none, 1..9 in range).</summary>
public sealed record ItemPrioritySet(Guid ItemId, int Priority, DateTimeOffset OccurredAt, Guid CommandId);
