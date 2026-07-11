using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Dtos.Me;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// Identity provisioning. <c>GET /me</c> resolves the caller's <see cref="LupiraTasksApi.Domain.Identity.Principal"/>
/// from the bearer token via <see cref="PrincipalDirectory"/> (create on first login, else refresh
/// email/displayName/last-seen), and returns the stable principal id plus display fields. The display
/// name is owned by the identity provider (Authentik) and is never client-editable; <c>IsAdmin</c> is
/// derived live from the token groups.
/// </summary>
public sealed class MeHandler
{
    private readonly CurrentUser _user;
    private readonly PrincipalDirectory _directory;

    public MeHandler(CurrentUser user, PrincipalDirectory directory)
    {
        _user = user;
        _directory = directory;
    }

    public async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> GetAsync(CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null)
        {
            return TypedResults.Unauthorized();
        }

        var principal = await _directory.ResolveOrProvisionAsync(_user.Sub, email, _user.DisplayName, ct);

        return TypedResults.Ok(new MeResponse
        {
            PrincipalId = principal.Id,
            Email = principal.Email,
            DisplayName = principal.DisplayName,
            IsAdmin = _user.IsAdmin,
        });
    }
}
