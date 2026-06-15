using LupiraTasksApi.Auth;

namespace LupiraTasksApi.Application;

/// <summary>
/// The authenticated caller, reduced to the transport-neutral facts the service layer
/// needs: their <see cref="Email"/> (= OIDC subject) and group memberships. Built by each
/// surface's adapter from the validated principal — <see cref="CurrentUser"/> on REST/MCP
/// (the JWT bearer) — so the services never touch <c>HttpContext</c> and can be driven
/// identically from REST handlers and MCP tools.
/// </summary>
public sealed record Caller(string Email, IReadOnlyList<string> Groups)
{
    /// <summary>True when the caller belongs to an admin group (shared list lives on <see cref="CurrentUser"/>).</summary>
    public bool IsAdmin => Groups.Any(g => CurrentUser.AdminGroups.Contains(g, StringComparer.OrdinalIgnoreCase));
}
