namespace LupiraTasksApi.Auth;

/// <summary>
/// RFC 9728 protected-resource metadata for the MCP surface (MCP auth spec): a 401 on <c>/mcp</c> points
/// clients here via <c>WWW-Authenticate: … resource_metadata=…</c>, and the document names the Authentik
/// authorization server. Authentik has no dynamic client registration, so clients still pre-register a
/// client id — this only makes discovery, not registration, automatic. Anonymous by design; it discloses
/// nothing but the issuer.
/// </summary>
public static class McpResourceMetadata
{
    public static IEndpointRouteBuilder MapMcpResourceMetadata(this IEndpointRouteBuilder app, string? authority)
    {
        // Both paths: RFC 9728 path-suffixes the resource's path (/mcp); some clients probe the root document.
        Func<HttpContext, IResult> handler = ctx => TypedResults.Ok(new
        {
            resource = $"{ctx.Request.Scheme}://{ctx.Request.Host}/mcp",
            authorization_servers = authority is { Length: > 0 } a ? new[] { a } : Array.Empty<string>(),
            bearer_methods_supported = new[] { "header" },
            scopes_supported = new[] { "openid", "profile", "email", "offline_access" },
        });
        app.MapGet("/.well-known/oauth-protected-resource", handler).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/.well-known/oauth-protected-resource/mcp", handler).AllowAnonymous().ExcludeFromDescription();
        return app;
    }

    public static string ResourceMetadataUrl(HttpRequest r) =>
        $"{r.Scheme}://{r.Host}/.well-known/oauth-protected-resource/mcp";
}
