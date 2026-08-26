using LupiraTasksApi.Core.Domain;

namespace LupiraTasksApi.Core.Dtos.Lists;

/// <summary>Full list metadata including members and tag definitions.</summary>
public sealed class ListResponse
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required ListKind Kind { get; set; }
    public string? Color { get; set; }
    public required bool SimplePriority { get; set; }
    public required PersonRef Owner { get; set; }
    /// <summary>The caller's own role on this list — server-authoritative, so clients gate owner/editor
    /// UI on this instead of matching themselves against <see cref="Members"/>.</summary>
    public required ListRole Access { get; set; }

    /// <summary>The caller's own position for this list (fractional-index key), or null if they have
    /// never reordered it — clients sort those by name, after the keyed ones. Nobody else's ordering
    /// is exposed.</summary>
    public string? SortOrder { get; set; }

    public required bool IsArchived { get; set; }

    /// <summary>When the list was archived; null while active. Archived views sort on this rather than
    /// <see cref="UpdatedAt"/>, which later edits bump.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public required IReadOnlyList<TagResponse> Tags { get; set; }
    public required IReadOnlyList<MemberResponse> Members { get; set; }
}
