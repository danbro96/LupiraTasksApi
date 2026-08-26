namespace LupiraTasksApi.Core.Domain.Lists.Events;

/// <summary>One member's own position for this list in their lists screen (a fractional-index key).
/// Per-member: reordering never moves the list for anyone else.</summary>
public sealed record MemberListOrderSet(Guid ListId, Guid PrincipalId, string SortOrder);
