namespace LupiraTasksApi.Core.Domain.Items.Events;

public sealed record ItemReopened(Guid ItemId, DateTimeOffset OccurredAt, Guid CommandId);
