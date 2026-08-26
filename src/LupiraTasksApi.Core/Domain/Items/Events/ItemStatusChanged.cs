namespace LupiraTasksApi.Core.Domain.Items.Events;

/// <summary>
/// Sets the item's lifecycle <see cref="ItemStatus"/> (and optional reason). The general lifecycle setter:
/// <see cref="ItemCompleted"/>/<see cref="ItemReopened"/> are intent-revealing shorthands that project onto the
/// same single status guard, so a status change and a complete/reopen converge as one field by (OccurredAt, CommandId).
/// </summary>
public sealed record ItemStatusChanged(Guid ItemId, ItemStatus Status, string? Reason, DateTimeOffset OccurredAt, Guid CommandId);
