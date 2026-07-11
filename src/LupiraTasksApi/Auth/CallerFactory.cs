using LupiraTasksApi.Application;

namespace LupiraTasksApi.Auth;

/// <summary>
/// The single funnel from a JWT principal to an application <see cref="Caller"/>: reads the
/// <c>sub</c>/<c>email</c>/<c>name</c> claims via <see cref="CurrentUser"/>, resolves (and JIT-provisions)
/// the internal <see cref="LupiraTasksApi.Domain.Identity.Principal"/> via <see cref="PrincipalDirectory"/>,
/// and returns a member caller keyed by the stable principal id. Every member surface (REST handlers, MCP
/// tools) builds its caller here so identity resolution lives in one place.
/// </summary>
public sealed class CallerFactory
{
    private readonly CurrentUser _user;
    private readonly PrincipalDirectory _directory;

    public CallerFactory(CurrentUser user, PrincipalDirectory directory)
    {
        _user = user;
        _directory = directory;
    }

    /// <summary>The resolved member caller, or <c>null</c> when the request carries no email claim
    /// (unauthenticated) — handlers surface that as 401.</summary>
    public async Task<Caller?> MemberAsync(CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return null;
        var principal = await _directory.ResolveOrProvisionAsync(_user.Sub, email, _user.DisplayName, ct);
        return Caller.Member(principal.Id, principal.Email, _user.Groups);
    }
}
