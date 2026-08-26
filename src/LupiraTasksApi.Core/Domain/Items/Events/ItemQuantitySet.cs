namespace LupiraTasksApi.Core.Domain.Items.Events;

public sealed record ItemQuantitySet(Guid ItemId, decimal? Quantity, string? Unit, DateTimeOffset OccurredAt, Guid CommandId);
