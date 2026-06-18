using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using LupiraTasksApi.Dav;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LupiraTasksApi.Auth;

/// <summary>
/// HTTP Basic auth for the <c>/dav</c> surface — DAV clients (DAVx5, iOS/macOS) can't do OIDC. The
/// decoded email becomes the principal's name claim, so the existing email-keyed <see cref="CurrentUser"/>
/// and list membership work unchanged (DAV and OIDC converge on the same email identity).
///
/// Production: the password is bound against the Authentik LDAP outpost — search as the reader service
/// account for the user by mail, then re-bind as that user DN with the supplied password. A successful
/// bind also implies membership of the gating group (the outpost only lets bound members search/bind).
/// In Development any password is accepted (login = email) so the surface is testable without LDAP.
///
/// Copied from LupiraCalApi's proven handler; only the realm + namespace differ.
/// </summary>
public sealed class DavBasicAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _config;

    public DavBasicAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        IHostEnvironment env, IConfiguration config)
        : base(options, logger, encoder)
    {
        _env = env;
        _config = config;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header)) return Task.FromResult(AuthenticateResult.NoResult());
        var value = header.ToString();
        if (!value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(AuthenticateResult.NoResult());

        if (!TryParseBasicCredentials(value, out var email, out var password))
            return Task.FromResult(AuthenticateResult.Fail("Malformed or missing Basic credentials."));

        var bound = _env.IsDevelopment() || LdapBind(email, password);
        if (!bound) return Task.FromResult(AuthenticateResult.Fail("Invalid credentials."));

        // Build the identity with email as the name claim so CurrentUser.Email (reads Identity.Name)
        // and list membership (keyed on email) resolve without a separate DAV identity path.
        var identity = new ClaimsIdentity(
            [new Claim("email", email), new Claim(ClaimTypes.Email, email)],
            authenticationType: DavConstants.Scheme, nameType: "email", roleType: "groups");
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, DavConstants.Scheme)));
    }

    /// <summary>Reader-search then user-bind against the Authentik LDAP outpost. Returns false on any failure.</summary>
    private bool LdapBind(string email, string password)
    {
        var uri = _config["Ldap:Uri"] ?? "ldap://authentik-ldap:3389";
        var baseDn = _config["Ldap:BaseDn"] ?? "dc=ldap,dc=goauthentik,dc=io";
        var readerDn = _config["Ldap:ReaderDn"];
        var readerSecret = _config["Ldap:ReaderSecret"];
        var filterTemplate = _config["Ldap:Filter"] ?? "(&(objectClass=user)(mail={0}))";
        if (string.IsNullOrEmpty(readerDn) || string.IsNullOrEmpty(readerSecret))
        {
            Logger.LogWarning("LDAP not configured (Ldap:ReaderDn / Ldap:ReaderSecret missing); rejecting DAV login.");
            return false;
        }

        try
        {
            var (host, port) = ParseLdapUri(uri);
            var identifier = new LdapDirectoryIdentifier(host, port);

            // 1) Bind as the reader service account and resolve the user's DN by mail.
            string userDn;
            using (var search = new LdapConnection(identifier) { AuthType = AuthType.Basic })
            {
                search.SessionOptions.ProtocolVersion = 3;
                search.Bind(new NetworkCredential(readerDn, readerSecret));

                var filter = filterTemplate.Replace("{0}", EscapeLdapFilter(email));
                var resp = (SearchResponse)search.SendRequest(new SearchRequest(baseDn, filter, SearchScope.Subtree, "1.1"));
                if (resp.Entries.Count == 0) return false;
                userDn = resp.Entries[0].DistinguishedName;
            }

            // 2) Re-bind as the user with the supplied password (throws LdapException on bad credentials).
            using var auth = new LdapConnection(identifier) { AuthType = AuthType.Basic };
            auth.SessionOptions.ProtocolVersion = 3;
            auth.Bind(new NetworkCredential(userDn, password));
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "LDAP bind failed for {Email}.", email);
            return false;
        }
    }

    /// <summary>Decodes an HTTP <c>Basic</c> authorization header into a (lowercased, trimmed) email + password.
    /// Returns false on a non-<c>Basic</c> header, invalid base64, a missing <c>:</c> separator, or an empty
    /// email/password. The split is on the <em>first</em> colon, so passwords may themselves contain colons.</summary>
    internal static bool TryParseBasicCredentials(string authorizationHeaderValue, out string email, out string password)
    {
        email = "";
        password = "";
        if (string.IsNullOrEmpty(authorizationHeaderValue) ||
            !authorizationHeaderValue.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorizationHeaderValue["Basic ".Length..].Trim()));
            var sep = decoded.IndexOf(':');
            if (sep < 0) return false;
            email = decoded[..sep].Trim().ToLowerInvariant();
            password = decoded[(sep + 1)..];
        }
        catch
        {
            return false;
        }
        return email.Length > 0 && password.Length > 0;
    }

    internal static (string Host, int Port) ParseLdapUri(string uri)
    {
        var u = new Uri(uri);
        return (u.Host, u.Port > 0 ? u.Port : 389);
    }

    // RFC 4515 filter-value escaping to prevent LDAP filter injection via the username.
    internal static string EscapeLdapFilter(string s) => s
        .Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Basic realm=\"lupira-tasks-dav\", charset=\"UTF-8\"";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
