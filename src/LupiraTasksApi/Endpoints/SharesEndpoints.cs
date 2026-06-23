using LupiraTasksApi.Dtos.Shares;
using LupiraTasksApi.Handlers;

namespace LupiraTasksApi.Endpoints;

public static class SharesEndpoints
{
    public static IEndpointRouteBuilder MapShares(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/lists/{listId:guid}/shares")
            .RequireAuthorization()
            .WithTags("Shares");

        group.MapPost("/", (HttpContext ctx, Guid listId, CreateShareRequest body, SharesHandler h, CancellationToken ct) =>
                h.CreateAsync(ctx, listId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Mint a public share link for a list (Owner).")
            .WithDescription("Body `{ access: 'Read' | 'ReadWrite', label?, expiresAt? }`. Returns the opaque token + a ready-to-copy URL. The link grants account-less access at `/shared/{token}` until revoked or expired.")
            .Produces<ShareResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/", (Guid listId, SharesHandler h, CancellationToken ct) =>
                h.ListAsync(listId, ct))
            .WithSummary("List a list's active share links (Owner).")
            .Produces<ShareCollectionResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{shareId:guid}", (HttpContext ctx, Guid listId, Guid shareId, SharesHandler h, CancellationToken ct) =>
                h.RevokeAsync(ctx, listId, shareId, ct))
            .WithIdempotencyKey()
            .WithSummary("Revoke a share link (Owner). The token is rejected on its next use.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        // Member-side redemption: an authenticated caller "cashes in" a share token to join the list.
        // JWT-authed (the default scheme) — distinct from the account-less ShareToken `/shared/{token}`
        // surface, and a different path segment ("shares" ≠ "shared") so the ShareToken handler ignores it.
        app.MapPost("/shares/redeem", (HttpContext ctx, RedeemShareRequest body, SharesHandler h, CancellationToken ct) =>
                h.RedeemAsync(ctx, body, ct))
            .RequireAuthorization()
            .WithTags("Shares")
            .WithIdempotencyKey()
            .WithSummary("Redeem a share link as the authenticated caller (join the list).")
            .WithDescription("Body `{ token }`. Adds the caller to the linked list — `ReadWrite` → Editor, `Read` → Viewer — and returns `{ listId, role }`. Idempotent: an existing member keeps their current role. Used by the web client to cash in a share link after SSO.")
            .Produces<RedeemShareResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
