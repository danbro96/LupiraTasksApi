namespace LupiraTasksApi.Core.Domain.Lists;

/// <summary>A user's membership of a list, keyed by the internal principal id.</summary>
public sealed class Member
{
    public Guid PrincipalId { get; set; }
    public ListRole Role { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    /// <summary>The actor who added them (a principal id, or <c>share:{label}</c>).</summary>
    public string? AddedBy { get; set; }

    /// <summary>This member's own position for the list in their lists screen (fractional-index key).
    /// Null until they first reorder; the clients sort those by name.</summary>
    public string? SortOrder { get; set; }
}
