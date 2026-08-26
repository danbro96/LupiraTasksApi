namespace LupiraTasksApi.Core.Domain.Lists.Events;

public sealed record TagRecolored(Guid ListId, Guid TagId, string Color);
