namespace LupiraTasksApi.Auth;

/// <summary>
/// Binds the <c>Auth:Oidc</c> configuration section. The OIDC provider
/// (Authentik) issues bearer tokens validated against this Authority/Audience.
/// </summary>
public sealed class OidcAuthOptions
{
    public const string SectionName = "Auth:Oidc";

    /// <summary>OIDC issuer/authority URL, e.g. the Authentik application endpoint.</summary>
    public string? Authority { get; set; }

    /// <summary>Expected token audience.</summary>
    public string? Audience { get; set; }
}
