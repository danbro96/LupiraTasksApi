using LupiraTasksApi.Domain;

namespace LupiraTasksApi.Dtos.Lists;

/// <summary>A tag definition on a list.</summary>
public sealed class TagResponse
{
    public required Guid Id { get; set; }
    public required string Label { get; set; }
    public required string Color { get; set; }
}

/// <summary>A member of a list: the stable <c>PrincipalId</c> plus resolved <c>Email</c>/<c>DisplayName</c>.</summary>
public sealed class MemberResponse
{
    public required Guid PrincipalId { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public required ListRole Role { get; set; }
    public required DateTimeOffset AddedAt { get; set; }
    /// <summary>Who added them; <c>null</c> for a share-link add or an unresolved actor.</summary>
    public PersonRef? AddedBy { get; set; }
}

/// <summary>Full list metadata including members and tag definitions.</summary>
public sealed class ListResponse
{
    public required Guid Id { get; set; }
    public required int Version { get; set; }
    public required string Name { get; set; }
    public required ListKind Kind { get; set; }
    public string? Color { get; set; }
    public required bool SimplePriority { get; set; }
    public required PersonRef Owner { get; set; }
    /// <summary>The caller's own role on this list — server-authoritative, so clients gate owner/editor
    /// UI on this instead of matching themselves against <see cref="Members"/>.</summary>
    public required ListRole Access { get; set; }
    public required bool IsArchived { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public required IReadOnlyList<TagResponse> Tags { get; set; }
    public required IReadOnlyList<MemberResponse> Members { get; set; }
}

/// <summary>Envelope for the caller's lists.</summary>
public sealed class ListCollectionResponse
{
    public required IReadOnlyList<ListResponse> Lists { get; set; }
}
