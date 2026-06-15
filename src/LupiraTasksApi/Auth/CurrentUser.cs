using System.Security.Claims;

namespace LupiraTasksApi.Auth;

/// <summary>
/// Scoped accessor for the authenticated caller, read from the validated JWT.
/// Bound to the request's <see cref="ClaimsPrincipal"/> via <see cref="IHttpContextAccessor"/>;
/// it never touches the database — provisioning (upserting <c>UserProfile</c>) happens
/// only on the explicit <c>GET /me</c> call, not per request.
///
/// The bearer is configured with <c>NameClaimType = "email"</c> and
/// <c>RoleClaimType = "groups"</c> (see <c>Program.cs</c>), so the OIDC subject is the
/// caller's email and group membership drives <see cref="IsAdmin"/>.
/// </summary>
public sealed class CurrentUser
{
    /// <summary>Authentik groups that grant admin rights. Shared with <c>Application.Caller</c> as the single source.</summary>
    internal static readonly string[] AdminGroups = ["tasks-admins", "platform-admins"];

    private readonly IHttpContextAccessor _accessor;

    public CurrentUser(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    /// <summary>True when a validated bearer principal is present.</summary>
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    /// <summary>
    /// The caller's email (= OIDC subject), read from the configured name claim.
    /// <c>null</c> when unauthenticated or the token carries no email claim.
    /// </summary>
    public string? Email
    {
        get
        {
            // Keyed strictly on the configured NameClaimType ("email") surfaced as
            // Identity.Name — no multi-source fallback, so identity has a single
            // authoritative source.
            var raw = Principal?.Identity?.Name;
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
    }

    /// <summary>The OIDC-supplied display name, if any.</summary>
    public string? DisplayName
    {
        get
        {
            var raw = Principal?.FindFirstValue("name")
                ?? Principal?.FindFirstValue(ClaimTypes.GivenName);
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
    }

    /// <summary>The caller's group memberships (RoleClaimType = "groups").</summary>
    public IReadOnlyList<string> Groups =>
        Principal?.FindAll("groups").Select(c => c.Value).ToArray() ?? [];

    /// <summary>True when the caller belongs to an admin group.</summary>
    public bool IsAdmin => Groups.Any(g => AdminGroups.Contains(g, StringComparer.OrdinalIgnoreCase));

    /// <summary>The caller's email or throws — for handlers that have already passed <c>[Authorize]</c>.</summary>
    public string RequireEmail() =>
        Email ?? throw new InvalidOperationException("Authenticated caller has no email claim.");
}
