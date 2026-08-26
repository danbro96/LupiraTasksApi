using LupiraTasksApi.Core.Domain;
using LupiraTasksApi.Core.Domain.Lists;

namespace LupiraTasksApi.Core.Auth;

/// <summary>
/// The outcome of a membership check. On success <see cref="List"/> and
/// <see cref="Role"/> are populated; on failure <see cref="List"/> is <c>null</c>
/// and the caller should return <c>404 Not Found</c> — never <c>403</c> — so the
/// existence of a list the caller can't see is not leaked.
/// </summary>
public readonly struct AccessResult
{
    public TodoList? List { get; init; }

    public ListRole Role { get; init; }

    public bool Allowed => List is not null;

    public static AccessResult Denied => new() { List = null };

    public static AccessResult Granted(TodoList list, ListRole role) => new() { List = list, Role = role };
}
