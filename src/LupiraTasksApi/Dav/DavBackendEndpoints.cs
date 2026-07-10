namespace LupiraTasksApi.Dav;

/// <summary>
/// The internal DAV-backend seam (docs/dav-backend-contract.md) the LupiraDavApi gateway consumes.
/// LAN-only (not tunneled + CF-header backstop) and service-authed: the caller is the DAV gateway's
/// client-credentials identity, gated by <c>DavBackendPolicy</c> (aud + azp). Excluded from the
/// public OpenAPI document.
/// </summary>
public static class DavBackendEndpoints
{
    public static IEndpointRouteBuilder MapDavBackend(this IEndpointRouteBuilder app)
    {
        // Rate-limiting is disabled: the gateway fans out many requests during a single device sync and
        // would trip the per-caller limiter; ETag/sync-token make those reads cheap.
        var group = app.MapGroup("/dav-backend/u/{email}")
            .RequireAuthorization("DavBackendPolicy")
            .DisableRateLimiting()
            .ExcludeFromDescription();

        group.MapGet("/collections",
            (string email, DavBackendHandler h, CancellationToken ct) => h.CollectionsAsync(email, ct));

        group.MapPost("/collections/{collectionId:guid}/query",
            (string email, Guid collectionId, DavQueryRequest body, DavBackendHandler h, CancellationToken ct) =>
                h.QueryAsync(email, collectionId, body, ct));

        group.MapGet("/collections/{collectionId:guid}/resources/{uid}",
            (string email, Guid collectionId, string uid, DavBackendHandler h, HttpContext ctx, CancellationToken ct) =>
                h.GetResourceAsync(email, collectionId, uid, ctx, ct));

        group.MapPut("/collections/{collectionId:guid}/resources/{uid}",
            (string email, Guid collectionId, string uid, DavBackendHandler h, HttpContext ctx, CancellationToken ct) =>
                h.PutResourceAsync(email, collectionId, uid, ctx, ct));

        group.MapDelete("/collections/{collectionId:guid}/resources/{uid}",
            (string email, Guid collectionId, string uid, DavBackendHandler h, HttpContext ctx, CancellationToken ct) =>
                h.DeleteResourceAsync(email, collectionId, uid, ctx, ct));

        group.MapGet("/collections/{collectionId:guid}/changes",
            (string email, Guid collectionId, string? since, DavBackendHandler h, CancellationToken ct) =>
                h.ChangesAsync(email, collectionId, since, ct));

        return app;
    }
}
