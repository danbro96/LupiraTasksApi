using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain.Identity;
using LupiraTasksApi.Dtos.Me;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// Identity provisioning. <c>GET /me</c> upserts the caller's <see cref="UserProfile"/>
/// from the bearer token (create on first login, else bump <c>LastSeenAt</c>) and always
/// refreshes <c>DisplayName</c>/<c>IsAdmin</c> from the SSO claims. The display name is
/// owned by the identity provider (Authentik) and is never client-editable.
/// </summary>
public sealed class MeHandler
{
    private readonly IDocumentSession _session;
    private readonly CurrentUser _user;

    public MeHandler(IDocumentSession session, CurrentUser user)
    {
        _session = session;
        _user = user;
    }

    public async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> GetAsync(CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null)
        {
            return TypedResults.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow;
        var profile = await _session.LoadAsync<UserProfile>(email, ct)
            ?? new UserProfile { Id = email, CreatedAt = now };

        // Name and admin flag are always sourced from the SSO token, never custom-set.
        profile.DisplayName = _user.DisplayName;
        profile.IsAdmin = _user.IsAdmin;
        profile.LastSeenAt = now;

        _session.Store(profile);
        await _session.SaveChangesAsync(ct);

        return TypedResults.Ok(ToResponse(profile));
    }

    private static MeResponse ToResponse(UserProfile p) => new()
    {
        Email = p.Id,
        DisplayName = p.DisplayName,
        IsAdmin = p.IsAdmin,
    };
}
