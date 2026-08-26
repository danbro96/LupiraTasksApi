namespace LupiraTasksApi.Core.Domain.Items.Events;

/// <summary>Commutative remove delta — resolved against <see cref="ItemTagAdded"/> by per-tag (OccurredAt, CommandId).</summary>
public sealed record ItemTagRemoved(Guid ItemId, Guid TagId, DateTimeOffset OccurredAt, Guid CommandId);
