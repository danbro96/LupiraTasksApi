namespace LupiraTasksApi.Core.Domain.Lists.Events;

public sealed record ListCreated(Guid ListId, string Name, ListKind Kind, string? Color, Guid OwnerPrincipalId);
