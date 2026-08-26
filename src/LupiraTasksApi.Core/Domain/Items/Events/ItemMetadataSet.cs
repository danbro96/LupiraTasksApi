namespace LupiraTasksApi.Core.Domain.Items.Events;

/// <summary>Sets the whole <see cref="ItemState.Metadata"/> JSON blob (server-side bookkeeping). Whole-field LWW.</summary>
public sealed record ItemMetadataSet(Guid ItemId, string? Metadata, DateTimeOffset OccurredAt, Guid CommandId);
