namespace LupiraTasksApi.Core.Domain.Lists.Events;

/// <summary>Sets whether the list treats priority as a simple on/off (true) or the full 0..9 scale (false).</summary>
public sealed record ListSimplePrioritySet(Guid ListId, bool SimplePriority);
