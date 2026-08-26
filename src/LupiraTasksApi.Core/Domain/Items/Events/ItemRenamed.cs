namespace LupiraTasksApi.Core.Domain.Items.Events;

public sealed record ItemRenamed(Guid ItemId, string Title, DateTimeOffset OccurredAt, Guid CommandId);
