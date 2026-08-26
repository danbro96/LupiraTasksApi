using LupiraTasksApi.Core.Domain;

namespace LupiraTasksApi.Core.Dtos.Shared;

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

    public required bool SimplePriority { get; set; }

    public required ShareAccess Access { get; set; }

    public required IReadOnlyList<SharedTagDto> Tags { get; set; }

    public required IReadOnlyList<SharedItemDto> Items { get; set; }
}
