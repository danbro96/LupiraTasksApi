namespace LupiraTasksApi.Core.Domain.Items.Events;

public sealed record ItemCompleted(Guid ItemId, DateTimeOffset OccurredAt, Guid CommandId);
