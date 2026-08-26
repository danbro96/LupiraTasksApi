namespace LupiraTasksApi.Core.Domain.Lists.Events;

public sealed record MemberRemoved(Guid ListId, Guid PrincipalId);
