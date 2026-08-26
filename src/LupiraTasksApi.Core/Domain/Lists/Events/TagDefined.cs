namespace LupiraTasksApi.Core.Domain.Lists.Events;

public sealed record TagDefined(Guid ListId, Guid TagId, string Label, string Color);
