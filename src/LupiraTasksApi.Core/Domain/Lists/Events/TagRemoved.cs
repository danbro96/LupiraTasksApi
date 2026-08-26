namespace LupiraTasksApi.Core.Domain.Lists.Events;

public sealed record TagRemoved(Guid ListId, Guid TagId);
