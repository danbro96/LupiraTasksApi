namespace LupiraTasksApi.Core.Domain.Items.Events;

public sealed record ItemAssigned(Guid ItemId, Guid? AssigneePrincipalId, DateTimeOffset OccurredAt, Guid CommandId);
