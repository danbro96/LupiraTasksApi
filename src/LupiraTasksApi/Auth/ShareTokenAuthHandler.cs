using LupiraTasksApi.Application;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Shares;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace LupiraTasksApi.Auth;

/// <summary>
/// Authenticates account-less share-link recipients on the <c>/shared/{token}</c> surface. The token
/// lives in the URL; this handler parses it from the path, loads the <see cref="ShareLink"/> by its
/// unique token, and — re-checking on EVERY request, so revoke/expiry take effect immediately —
/// <c>Fail</c>s if the link is missing, revoked, or expired. On success it issues a principal carrying
/// the share-grant claims (no email), which the shared handlers turn into a <c>Caller.ForShare(...)</c>.
/// Only acts on <c>/shared/</c> paths (else <c>NoResult</c>), so it never interferes with the JWT/Dev
/// schemes that secure the rest of the API.
/// </summary>
public sealed class ShareTokenAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ShareToken";
    private const string PathPrefix = "/shared";

    public const string ShareIdClaim = "share-id";
    public const string ListIdClaim = "share-list-id";
    public const string AccessClaim = "share-access";
    public const string LabelClaim = "share-label";

    private readonly IQuerySession _query;

    public ShareTokenAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IQuerySession query)
        : base(options, logger, encoder)
    {
        _query = query;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Path.StartsWithSegments(PathPrefix, out var rest) || !rest.HasValue)
            return AuthenticateResult.NoResult();

        // rest is "/{token}" or "/{token}/items/...": take the first segment.
        var token = rest.Value!.Trim('/').Split('/', 2)[0];
        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        var link = await _query.Query<ShareLink>()
            .Where(s => s.Token == token)
            .FirstOrDefaultAsync(Context.RequestAborted);

        if (link is null)
            return AuthenticateResult.Fail("Unknown share token.");
        if (!link.IsActive(DateTimeOffset.UtcNow))
            return AuthenticateResult.Fail("Share link is revoked or expired.");

        var claims = new[]
        {
            new Claim(ShareIdClaim, link.Id.ToString()),
            new Claim(ListIdClaim, link.ListId.ToString()),
            new Claim(AccessClaim, link.Access.ToString()),
            new Claim(LabelClaim, link.Label),
            new Claim(ClaimTypes.Name, $"share:{link.Label}"),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>Reconstruct the share grant from an authenticated share principal, or <c>null</c> if absent/malformed.</summary>
    public static ShareGrant? GrantFrom(ClaimsPrincipal principal)
    {
        var shareId = principal.FindFirst(ShareIdClaim)?.Value;
        var listId = principal.FindFirst(ListIdClaim)?.Value;
        var access = principal.FindFirst(AccessClaim)?.Value;
        var label = principal.FindFirst(LabelClaim)?.Value;

        if (shareId is null || listId is null || access is null || label is null) return null;
        if (!Guid.TryParse(shareId, out var sid)
            || !Guid.TryParse(listId, out var lid)
            || !Enum.TryParse<ShareAccess>(access, out var acc))
        {
            return null;
        }

        return new ShareGrant(sid, lid, acc, label);
    }
}
