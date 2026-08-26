namespace LupiraTasksApi.Core.Domain.Lists.Events;

public sealed record MemberRoleChanged(Guid ListId, Guid PrincipalId, ListRole Role);
