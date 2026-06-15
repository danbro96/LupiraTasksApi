using LupiraTasksApi.Domain;

namespace LupiraTasksApi.Models.Shared;

/// <summary>A tag definition as shown on a shared list (no sensitive data).</summary>
public sealed class SharedTagResponse
{
    public required Guid Id { get; set; }
    public required string Label { get; set; }
    public required string Color { get; set; }
}

/// <summary>
/// The public, account-less view of a shared list. Deliberately TRIMMED: it carries no member
/// list, no owner email, and items carry no assignee/creator/completer emails — a public link must
/// not leak family emails. <see cref="Access"/> tells the client whether to show edit controls.
/// </summary>
public sealed class SharedListResponse
{
    public required string Name { get; set; }
    public required ListKind Kind { get; set; }
    public string? Color { get; set; }
    public required ShareAccess Access { get; set; }
    public required IReadOnlyList<SharedTagResponse> Tags { get; set; }
    public required IReadOnlyList<SharedItemResponse> Items { get; set; }
}
