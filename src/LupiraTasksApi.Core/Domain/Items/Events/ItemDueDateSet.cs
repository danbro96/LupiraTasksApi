namespace LupiraTasksApi.Core.Domain.Items.Events;

/// <summary>Schema v1 of <see cref="ItemDueDateSetV2"/>, read through its upcaster.</summary>
public sealed record ItemDueDateSet(Guid ItemId, DateTimeOffset? DueAt, DateTimeOffset OccurredAt, Guid CommandId);
