using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Lists;
using Marten;

namespace LupiraTasksApi.Auth;

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

/// <summary>
/// Scoped membership/authorization gate. Loads the inline <c>TodoList</c> snapshot
/// and checks the caller's role against a minimum (Owner &gt; Editor &gt; Viewer).
///
/// Returns "denied" (which handlers surface as <c>404</c>) for: a missing/deleted
/// list, a caller who isn't a member, or a member whose role is below the minimum.
/// Returning 404 rather than 403 means a non-member can't distinguish "no such list"
/// from "exists but you lack access".
/// </summary>
public sealed class AccessResolver
{
    private readonly IDocumentSession _session;

    public AccessResolver(IDocumentSession session)
    {
        _session = session;
    }

    /// <summary>
    /// Requires the caller to be a member of the list with at least <paramref name="minRole"/>.
    /// Loads and returns the list snapshot on success.
    /// </summary>
    public async Task<AccessResult> RequireMembershipAsync(
        Guid listId,
        string email,
        ListRole minRole,
        CancellationToken ct)
    {
        var list = await _session.LoadAsync<TodoList>(listId, ct);
        if (list is null || list.IsDeleted)
        {
            return AccessResult.Denied;
        }

        var member = list.Members.Find(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase));
        if (member is null || !Satisfies(member.Role, minRole))
        {
            return AccessResult.Denied;
        }

        return AccessResult.Granted(list, member.Role);
    }

    /// <summary>
    /// True when <paramref name="actual"/> meets or exceeds <paramref name="required"/>.
    /// The enum is ordered Owner(0) &gt; Editor(1) &gt; Viewer(2), so a lower numeric
    /// value is a higher privilege.
    /// </summary>
    public static bool Satisfies(ListRole actual, ListRole required) => actual <= required;
}
