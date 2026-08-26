namespace LupiraTasksApi.Core.Domain.Lists.Events;

public sealed record MemberAdded(Guid ListId, Guid PrincipalId, ListRole Role);
