namespace LupiraTasksApi.Core.Domain.Items.Events;

public sealed record ItemNotesEdited(Guid ItemId, string? Notes, DateTimeOffset OccurredAt, Guid CommandId);
