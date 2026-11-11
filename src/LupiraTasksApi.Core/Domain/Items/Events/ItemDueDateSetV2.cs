namespace LupiraTasksApi.Core.Domain.Items.Events;

/// <summary>Sets or clears the due: <see cref="DueOn"/> for a whole day, <see cref="DueAt"/> for an instant; at most one is set.</summary>
public sealed record ItemDueDateSetV2(Guid ItemId, DateOnly? DueOn, DateTimeOffset? DueAt, DateTimeOffset OccurredAt, Guid CommandId);
