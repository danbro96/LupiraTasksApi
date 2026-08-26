namespace LupiraTasksApi.Core.Domain.Items.Events;

/// <summary>Commutative add delta — resolved against <see cref="ItemTagRemoved"/> by per-tag (OccurredAt, CommandId).</summary>
public sealed record ItemTagAdded(Guid ItemId, Guid TagId, DateTimeOffset OccurredAt, Guid CommandId);
