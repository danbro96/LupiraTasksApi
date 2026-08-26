namespace LupiraTasksApi.Core.Domain.Items.Events;

/// <summary>Tombstone (stream retained). Once applied, later field events are ignored.</summary>
public sealed record ItemDeleted(Guid ItemId, DateTimeOffset OccurredAt, Guid CommandId);
