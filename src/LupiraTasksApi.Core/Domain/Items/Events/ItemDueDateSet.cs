namespace LupiraTasksApi.Core.Domain.Items.Events;

public sealed record ItemDueDateSet(Guid ItemId, DateTimeOffset? DueAt, DateTimeOffset OccurredAt, Guid CommandId);
