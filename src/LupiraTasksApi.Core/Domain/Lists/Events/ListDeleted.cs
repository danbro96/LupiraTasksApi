namespace LupiraTasksApi.Core.Domain.Lists.Events;

/// <summary>Tombstone. Auto-emitted when the last owner leaves the list.</summary>
public sealed record ListDeleted(Guid ListId, string Reason);
