namespace LupiraTasksApi.Core.Domain.Lists.Events;

public sealed record ListRenamed(Guid ListId, string Name);
