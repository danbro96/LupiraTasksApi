using LupiraTasksApi.Domain;

namespace LupiraTasksApi.Application;

/// <summary>
/// The authenticated caller, reduced to the transport-neutral facts the service layer needs.
/// It is one of two shapes:
/// <list type="bullet">
/// <item><b>Member</b> — a real user identified by <see cref="Email"/> (the OIDC subject) + groups;
/// built from the JWT bearer by each surface's adapter.</item>
/// <item><b>Share</b> — an account-less share-link recipient (<see cref="Share"/> is set,
/// <see cref="Email"/> is null), scoped to one list at one access level.</item>
/// </list>
/// Services authorize via <c>AccessResolver.AuthorizeAsync(caller, …)</c> and stamp <see cref="Actor"/>
/// into the event <c>actor</c> header, so the same code path serves members and share recipients.
/// </summary>
public sealed record Caller
{
    /// <summary>Authentik groups that grant admin rights — the single source for member admin checks.</summary>
    public static readonly string[] AdminGroups = ["tasks-admins", "platform-admins"];

    /// <summary>Member identity (OIDC subject). <c>null</c> for a share-link caller.</summary>
    public string? Email { get; private init; }

    public IReadOnlyList<string> Groups { get; private init; } = [];

    /// <summary>Share-link grant. <c>null</c> for a member caller.</summary>
    public ShareGrant? Share { get; private init; }

    private Caller() { }

    public static Caller Member(string email, IReadOnlyList<string> groups) =>
        new() { Email = email, Groups = groups };

    public static Caller ForShare(ShareGrant share) =>
        new() { Share = share };

    /// <summary>
    /// The value stamped into the event <c>actor</c> header: a member's email, or
    /// <c>share:{label}</c> for a share-link write (see <see cref="EventActor"/>).
    /// </summary>
    public string Actor => Share is { } s ? $"share:{s.Label}" : Email!;

    /// <summary>True only for a member in an admin group; a share-link caller is never admin.</summary>
    public bool IsAdmin =>
        Email is not null && Groups.Any(g => AdminGroups.Contains(g, StringComparer.OrdinalIgnoreCase));
}

/// <summary>A validated share-link grant: scoped to exactly one list at one access level.</summary>
public sealed record ShareGrant(Guid ShareId, Guid ListId, ShareAccess Access, string Label);
