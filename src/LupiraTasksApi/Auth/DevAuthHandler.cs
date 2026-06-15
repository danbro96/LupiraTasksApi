using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LupiraTasksApi.Auth;

/// <summary>
/// DEVELOPMENT-ONLY authentication. Lets the API (REST and the MCP surface) be exercised
/// without Authentik by trusting an <c>X-Dev-User</c> header (the caller's email) plus an
/// optional <c>X-Dev-Groups</c> (comma-separated group names).
///
/// <para>
/// Registered ONLY when the host environment is Development (see <c>Program.cs</c>), so it can
/// never authenticate a request in Production — prod runs <c>ASPNETCORE_ENVIRONMENT=Production</c>
/// (the aspnet base-image default; the deploy compose never sets Development), where this scheme
/// is not added at all.
/// </para>
///
/// <para>
/// Claims are shaped to match the JWT bearer's mapping — the email lands on the configured
/// <c>NameClaimType</c> ("email") and each group on the <c>RoleClaimType</c> ("groups") — so
/// <see cref="CurrentUser"/> and everything downstream behave identically to a real Authentik token.
/// </para>
/// </summary>
public sealed class DevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DevHeader";
    public const string HeaderName = "X-Dev-User";
    public const string GroupsHeaderName = "X-Dev-Groups";

    public DevAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var emailRaw))
        {
            // No dev header → let authorization challenge (401), same as a missing bearer.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var email = emailRaw.ToString().Trim();
        if (string.IsNullOrEmpty(email))
        {
            return Task.FromResult(AuthenticateResult.Fail($"'{HeaderName}' header is present but empty."));
        }

        var claims = new List<Claim> { new("email", email) };
        if (Request.Headers.TryGetValue(GroupsHeaderName, out var groupsRaw))
        {
            foreach (var group in groupsRaw.ToString()
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim("groups", group));
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName, nameType: "email", roleType: "groups");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
