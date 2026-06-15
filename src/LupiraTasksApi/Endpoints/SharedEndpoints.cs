using LupiraTasksApi.Auth;
using LupiraTasksApi.Handlers;
using LupiraTasksApi.Models.Items;
using LupiraTasksApi.Models.Shared;

namespace LupiraTasksApi.Endpoints;

public static class SharedEndpoints
{
    public static IEndpointRouteBuilder MapShared(this IEndpointRouteBuilder app)
    {
        // Public, account-less consumption of a share link. The `{token}` is authenticated by the
        // ShareToken scheme (which parses + validates it from the path); the route param itself is
        // unused by handlers, which read the resolved grant from the principal's claims. Write routes
        // require a read/write link (a read-only link gets 403) and reuse the same idempotency + LWW
        // as the main client (send `Idempotency-Key` + `occurredAt`).
        var group = app.MapGroup("/shared/{token}")
            .RequireAuthorization(ShareTokenAuthHandler.SchemeName)
            .WithTags("Shared");

        group.MapGet("", (SharedHandler h, CancellationToken ct) => h.GetListAsync(ct))
            .WithSummary("View a shared list by token (no account needed).")
            .WithDescription("Returns the list + items, trimmed of all emails. `access` indicates read vs read/write.")
            .Produces<SharedListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/items", (HttpContext ctx, CreateItemRequest body, SharedHandler h, CancellationToken ct) =>
                h.AddItemAsync(ctx, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Add an item via a read/write share link.")
            .Produces<SharedItemResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPatch("/items/{itemId:guid}", (HttpContext ctx, Guid itemId, UpdateItemRequest body, SharedHandler h, CancellationToken ct) =>
                h.UpdateItemAsync(ctx, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Edit an item via a read/write share link.")
            .Produces<SharedItemResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/items/{itemId:guid}/complete", (HttpContext ctx, Guid itemId, ItemTimestampRequest? body, SharedHandler h, CancellationToken ct) =>
                h.CompleteAsync(ctx, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Mark an item complete via a read/write share link.")
            .Produces<SharedItemResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/items/{itemId:guid}/reopen", (HttpContext ctx, Guid itemId, ItemTimestampRequest? body, SharedHandler h, CancellationToken ct) =>
                h.ReopenAsync(ctx, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Reopen a completed item via a read/write share link.")
            .Produces<SharedItemResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/items/{itemId:guid}/move", (HttpContext ctx, Guid itemId, MoveItemRequest body, SharedHandler h, CancellationToken ct) =>
                h.MoveAsync(ctx, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Reparent / reorder an item via a read/write share link.")
            .Produces<SharedItemResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/items/{itemId:guid}", (HttpContext ctx, Guid itemId, DateTimeOffset? occurredAt, SharedHandler h, CancellationToken ct) =>
                h.DeleteItemAsync(ctx, itemId, occurredAt, ct))
            .WithIdempotencyKey()
            .WithSummary("Delete an item via a read/write share link.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
